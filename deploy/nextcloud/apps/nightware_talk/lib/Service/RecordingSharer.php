<?php

declare(strict_types=1);

namespace OCA\NightwareTalk\Service;

use OCA\NightwareTalk\RecordingPath;
use OCA\Talk\Config as TalkConfig;
use OCA\Talk\Exceptions\ParticipantNotFoundException;
use OCA\Talk\Exceptions\RoomNotFoundException;
use OCA\Talk\Manager as RoomManager;
use OCA\Talk\Service\ParticipantService;
use OCP\App\IAppManager;
use OCP\Constants;
use OCP\Files\File;
use OCP\Files\IRootFolder;
use OCP\ISession;
use OCP\IUser;
use OCP\IUserManager;
use OCP\IUserSession;
use OCP\Share\IManager as ShareManager;
use OCP\Share\IShare;
use Psr\Container\ContainerInterface;
use Psr\Log\LoggerInterface;

/**
 * Shares a recording, transcript or summary with its conversation: the read-only room share (`IShare::TYPE_ROOM`,
 * shared with the token) that Talk's "Share to chat" makes, once per file. Talk then posts the file into the chat.
 */
class RecordingSharer {
	/** The session key Talk reads for who posts a system message when nobody is signed in. */
	private const ACTOR_OVERRIDE = 'talk-overwrite-actor-id';

	public function __construct(
		private readonly IAppManager $appManager,
		private readonly ContainerInterface $container,
		private readonly IRootFolder $rootFolder,
		private readonly IUserManager $userManager,
		private readonly IUserSession $userSession,
		private readonly ISession $session,
		private readonly ShareManager $shareManager,
		private readonly LoggerInterface $logger,
	) {
	}

	/**
	 * @param array{uid: string, relative: string, token: string, name: string, kind: string} $candidate what
	 *        {@see RecordingPath::candidate()} made of the file's path
	 */
	public function shareWithConversation(File $file, array $candidate): void {
		if (!$this->appManager->isEnabledForAnyone('spreed') || !class_exists(TalkConfig::class)) {
			return;
		}

		$uid = $candidate['uid'];
		$token = $candidate['token'];
		// Talk's own rule for where the user's recordings go (the attachment folder's Recording, or the user's
		// separate recording folder preference).
		$recordingFolder = $this->container->get(TalkConfig::class)->getRecordingFolder($uid);
		if (!RecordingPath::isInRecordingFolder($candidate['relative'], $recordingFolder, $token)) {
			return;
		}

		$owner = $this->userManager->get($uid);
		if ($owner === null || $file->getOwner()?->getUID() !== $uid) {
			return;
		}

		try {
			$room = $this->container->get(RoomManager::class)->getRoomForUserByToken($token, $uid);
			$this->container->get(ParticipantService::class)->getParticipant($room, $uid, false);
		} catch (RoomNotFoundException|ParticipantNotFoundException) {
			// Not a conversation the owner takes part in (any more).
			return;
		}

		// The file as its owner has it: the event's node can come through the recording server's upload link,
		// which may only create.
		$node = $this->rootFolder->getUserFolder($uid)->getFirstNodeById($file->getId());
		if (!$node instanceof File || $node->getSize() === 0) {
			return;
		}

		foreach ($this->shareManager->getSharesBy($uid, IShare::TYPE_ROOM, $node, false, -1) as $existing) {
			if ($existing->getSharedWith() === $token) {
				return;
			}
		}

		$share = $this->shareManager->newShare();
		$share->setNode($node)
			->setShareType(IShare::TYPE_ROOM)
			->setSharedWith($token)
			->setSharedBy($uid)
			->setPermissions(Constants::PERMISSION_READ);

		$this->asOwner($owner, fn () => $this->shareManager->createShare($share));

		$this->logger->info('Shared call {kind} "{name}" with conversation {token}', [
			'app' => 'nightware_talk',
			'kind' => $candidate['kind'],
			'name' => $candidate['name'],
			'token' => $token,
		]);
	}

	/**
	 * Talk posts the shared file into the chat as the signed-in user, and with nobody signed in as a guest. The
	 * recording server's chunked upload (a public link, where Nextcloud hides any user), its direct upload and the
	 * transcription (a background job) have nobody, so the owner is made the poster for this one call: the active
	 * user, and Talk's own session override for the public link (what Talk's circle listener sets).
	 */
	private function asOwner(IUser $owner, callable $action): void {
		$previous = $this->userSession->getUser();
		$overridden = false;
		try {
			if (!$this->session->exists(self::ACTOR_OVERRIDE)) {
				$this->session->set(self::ACTOR_OVERRIDE, $owner->getUID());
				$overridden = true;
			}
		} catch (\Throwable) {
			// No session to carry it: the active user alone has to do.
		}

		$this->userSession->setVolatileActiveUser($owner);
		try {
			$action();
		} finally {
			$this->userSession->setVolatileActiveUser($previous);
			if ($overridden) {
				try {
					$this->session->remove(self::ACTOR_OVERRIDE);
				} catch (\Throwable) {
					// Gone with the session.
				}
			}
		}
	}
}
