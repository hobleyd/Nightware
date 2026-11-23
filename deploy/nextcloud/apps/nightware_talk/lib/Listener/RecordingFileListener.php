<?php

declare(strict_types=1);

namespace OCA\NightwareTalk\Listener;

use OCA\NightwareTalk\RecordingPath;
use OCA\NightwareTalk\Service\RecordingSharer;
use OCP\EventDispatcher\Event;
use OCP\EventDispatcher\IEventListener;
use OCP\Files\Events\Node\NodeCreatedEvent;
use OCP\Files\Events\Node\NodeRenamedEvent;
use OCP\Files\Events\Node\NodeWrittenEvent;
use OCP\Files\File;
use Psr\Container\ContainerInterface;
use Psr\Log\LoggerInterface;

/**
 * A file that lands in a recording folder: Talk's direct upload writes it (created, then written), the recording
 * server's chunked upload assembles it, and the transcription writes the transcript and summary. Every path is
 * covered by created, written and renamed (the target); the share is made once, whichever comes first with content.
 * Never throws: a failure here must not fail the upload, so it is logged instead.
 *
 * @template-implements IEventListener<Event>
 */
class RecordingFileListener implements IEventListener {
	public function __construct(
		private readonly ContainerInterface $container,
		private readonly LoggerInterface $logger,
	) {
	}

	#[\Override]
	public function handle(Event $event): void {
		try {
			$node = match (true) {
				$event instanceof NodeRenamedEvent => $event->getTarget(),
				$event instanceof NodeCreatedEvent, $event instanceof NodeWrittenEvent => $event->getNode(),
				default => null,
			};
			if (!$node instanceof File) {
				return;
			}

			// Every file write on the instance comes through here: only strings until the path looks right.
			$candidate = RecordingPath::candidate($node->getPath());
			if ($candidate === null) {
				return;
			}

			$this->container->get(RecordingSharer::class)->shareWithConversation($node, $candidate);
		} catch (\Throwable $e) {
			$this->logger->error('Could not share a call recording with its conversation', ['app' => 'nightware_talk', 'exception' => $e]);
		}
	}
}
