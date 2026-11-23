<?php

declare(strict_types=1);

namespace OCA\NightwareTalk;

/**
 * Where Talk puts call recordings, as plain string rules (no Nextcloud classes, so plain `php` tests it).
 *
 * Talk's recording server stores `{recording folder}/{token}/{name}.{ogg|webm|…}` in the files of whoever recorded the
 * call; its transcription writes `{name}.md` beside it and a summary `{name} - summary.md`. The recording folder is the
 * user's Talk setting (`{attachment folder}/Recording` by default). The folder named after the room token is how Talk
 * itself tells which conversation a file belongs to.
 */
final class RecordingPath {
	public const RECORDING = 'recording';
	public const TRANSCRIPT = 'transcript';
	public const SUMMARY = 'summary';

	/** Talk's allowed recording formats (RecordingService::DEFAULT_ALLOWED_RECORDING_FORMATS). */
	private const MEDIA_EXTENSIONS = ['ogg', 'ogv', 'mp4', 'webm', 'mkv'];

	/** A conversation token as Talk's routes accept it. */
	private const TOKEN = '/^[a-z0-9]{4,30}$/';

	/**
	 * The first, cheap test, run on every file write: a node path `/{uid}/files/…/{token}/{name}` whose name is a
	 * recording, a transcript or a summary.
	 *
	 * @return array{uid: string, relative: string, token: string, name: string, kind: string}|null where `relative` is
	 *         the path below the user's files, without a leading slash
	 */
	public static function candidate(string $path): ?array {
		$segments = explode('/', ltrim($path, '/'));
		// uid, "files", the recording folder (any depth, even none), the token, the name
		if (count($segments) < 4 || $segments[0] === '' || $segments[1] !== 'files') {
			return null;
		}

		$name = $segments[count($segments) - 1];
		$token = $segments[count($segments) - 2];
		$kind = self::kind($name);
		if ($kind === null || preg_match(self::TOKEN, $token) !== 1) {
			return null;
		}

		return [
			'uid' => $segments[0],
			'relative' => implode('/', array_slice($segments, 2)),
			'token' => $token,
			'name' => $name,
			'kind' => $kind,
		];
	}

	/**
	 * Whether `$relative` (below the user's files) is a file directly in `{recordingFolder}/{token}/`. The recording
	 * folder is Talk's setting as it stores it (`/Talk/Recording`, or `//Recording` with the attachment folder at the
	 * root): slashes are not significant.
	 */
	public static function isInRecordingFolder(string $relative, string $recordingFolder, string $token): bool {
		$folder = self::normalise($recordingFolder);
		$expected = ($folder === '' ? '' : $folder . '/') . $token . '/';
		$relative = self::normalise($relative);
		return str_starts_with($relative, $expected) && !str_contains(substr($relative, strlen($expected)), '/');
	}

	/** What a file in a recording folder is, by its name; null for anything else (part files, hidden files, other types). */
	public static function kind(string $name): ?string {
		if ($name === '' || $name[0] === '.') {
			return null;
		}

		$dot = strrpos($name, '.');
		if ($dot === false || $dot === 0) {
			return null;
		}

		$extension = strtolower(substr($name, $dot + 1));
		if (in_array($extension, self::MEDIA_EXTENSIONS, true)) {
			return self::RECORDING;
		}

		if ($extension !== 'md') {
			return null;
		}

		return str_ends_with(strtolower(substr($name, 0, $dot)), ' - summary') ? self::SUMMARY : self::TRANSCRIPT;
	}

	private static function normalise(string $path): string {
		return implode('/', array_filter(explode('/', $path), static fn (string $s): bool => $s !== ''));
	}
}
