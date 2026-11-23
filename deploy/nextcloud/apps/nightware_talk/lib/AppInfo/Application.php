<?php

declare(strict_types=1);

namespace OCA\NightwareTalk\AppInfo;

use OCA\NightwareTalk\Listener\RecordingFileListener;
use OCP\AppFramework\App;
use OCP\AppFramework\Bootstrap\IBootContext;
use OCP\AppFramework\Bootstrap\IBootstrap;
use OCP\AppFramework\Bootstrap\IRegistrationContext;
use OCP\Files\Events\Node\NodeCreatedEvent;
use OCP\Files\Events\Node\NodeRenamedEvent;
use OCP\Files\Events\Node\NodeWrittenEvent;

class Application extends App implements IBootstrap {
	public const APP_ID = 'nightware_talk';

	public function __construct() {
		parent::__construct(self::APP_ID);
	}

	#[\Override]
	public function register(IRegistrationContext $context): void {
		$context->registerEventListener(NodeCreatedEvent::class, RecordingFileListener::class);
		$context->registerEventListener(NodeWrittenEvent::class, RecordingFileListener::class);
		$context->registerEventListener(NodeRenamedEvent::class, RecordingFileListener::class);
	}

	#[\Override]
	public function boot(IBootContext $context): void {
	}
}
