<?php

declare(strict_types=1);

// Plain-PHP tests of the app's path rules (no Nextcloud needed): php tests/RecordingPathTest.php
// Exits non-zero on the first failed expectation. tests/run.sh runs it inside the nextcloud container.

require __DIR__ . '/../lib/RecordingPath.php';

use OCA\NightwareTalk\RecordingPath;

$failures = 0;
$count = 0;

function expect(string $what, mixed $actual, mixed $expected): void {
	global $failures, $count;
	$count++;
	if ($actual !== $expected) {
		$failures++;
		fwrite(STDERR, "FAIL {$what}\n  expected: " . var_export($expected, true) . "\n  actual:   " . var_export($actual, true) . "\n");
	}
}

// --- kind: what a file in a recording folder is
expect('an Ogg recording', RecordingPath::kind('Recording 2026-11-23 09-59-18.ogg'), RecordingPath::RECORDING);
expect('a webm video recording', RecordingPath::kind('Recording 2026-11-23 09-59-18.webm'), RecordingPath::RECORDING);
expect('every format Talk accepts', array_map(RecordingPath::kind(...), ['a.ogv', 'a.mp4', 'a.mkv', 'a.OGG']), array_fill(0, 4, RecordingPath::RECORDING));
expect('a transcript', RecordingPath::kind('Recording 2026-11-23 09-59-18.md'), RecordingPath::TRANSCRIPT);
expect('a summary', RecordingPath::kind('Recording 2026-11-23 09-59-18 - summary.md'), RecordingPath::SUMMARY);
expect('a summary, any case', RecordingPath::kind('Recording 2026-11-23 09-59-18 - Summary.MD'), RecordingPath::SUMMARY);
expect('another type', RecordingPath::kind('notes.txt'), null);
expect('an upload part file', RecordingPath::kind('Recording 2026-11-23 09-59-18.ogg.ocTransferId123.part'), null);
expect('a hidden file', RecordingPath::kind('.Recording.ogg'), null);
expect('no extension', RecordingPath::kind('Recording'), null);
expect('only an extension', RecordingPath::kind('.ogg'), null);
expect('an empty name', RecordingPath::kind(''), null);

// --- candidate: the cheap first test on a node path
expect('a recording in the default folder', RecordingPath::candidate('/alice/files/Talk/Recording/g4qoq2me/Recording 2026-11-23 07-00-43.ogg'), [
	'uid' => 'alice',
	'relative' => 'Talk/Recording/g4qoq2me/Recording 2026-11-23 07-00-43.ogg',
	'token' => 'g4qoq2me',
	'name' => 'Recording 2026-11-23 07-00-43.ogg',
	'kind' => RecordingPath::RECORDING,
]);
expect('a transcript', RecordingPath::candidate('/bob/files/Talk/Recording/oygp6e5d/Recording 2026-11-23 10-30-00.md')['kind'] ?? null, RecordingPath::TRANSCRIPT);
expect('a recording folder at the root', RecordingPath::candidate('/alice/files/abcd1234/Recording.ogg')['relative'] ?? null, 'abcd1234/Recording.ogg');
expect('not below files', RecordingPath::candidate('/alice/files_trashbin/files/Talk/Recording/g4qoq2me/R.ogg.d123'), null);
expect('a version', RecordingPath::candidate('/alice/files_versions/Talk/Recording/g4qoq2me/R.ogg.v123'), null);
expect('the folder is no token (upper case)', RecordingPath::candidate('/alice/files/Talk/Recording/G4QOQ2ME/R.ogg'), null);
expect('the folder is no token (too short)', RecordingPath::candidate('/alice/files/Talk/Recording/abc/R.ogg'), null);
expect('the folder is no token (too long)', RecordingPath::candidate('/alice/files/Talk/Recording/' . str_repeat('a', 31) . '/R.ogg'), null);
expect('the folder is no token (a space)', RecordingPath::candidate('/alice/files/Talk/My files/R.ogg'), null);
expect('not a recording', RecordingPath::candidate('/alice/files/Talk/Recording/g4qoq2me/notes.txt'), null);
expect('too shallow', RecordingPath::candidate('/alice/files/R.ogg'), null);
expect('a user root', RecordingPath::candidate('/alice'), null);
expect('nothing', RecordingPath::candidate(''), null);

// --- isInRecordingFolder: Talk's folder setting decides
expect('the default folder', RecordingPath::isInRecordingFolder('Talk/Recording/g4qoq2me/R.ogg', '/Talk/Recording', 'g4qoq2me'), true);
expect('slashes are not significant', RecordingPath::isInRecordingFolder('/Talk/Recording/g4qoq2me/R.ogg', 'Talk/Recording/', 'g4qoq2me'), true);
expect('the attachment folder at the root', RecordingPath::isInRecordingFolder('Recording/g4qoq2me/R.ogg', '//Recording', 'g4qoq2me'), true);
expect('a recording folder at the root', RecordingPath::isInRecordingFolder('g4qoq2me/R.ogg', '/', 'g4qoq2me'), true);
expect('a custom attachment folder', RecordingPath::isInRecordingFolder('Chats/Recording/g4qoq2me/R.ogg', '/Chats/Recording', 'g4qoq2me'), true);
expect('another folder', RecordingPath::isInRecordingFolder('Documents/Recording/g4qoq2me/R.ogg', '/Talk/Recording', 'g4qoq2me'), false);
expect('a folder nested under the recording folder', RecordingPath::isInRecordingFolder('Talk/Recording/old/g4qoq2me/R.ogg', '/Talk/Recording', 'g4qoq2me'), false);
expect('a subfolder of the token folder', RecordingPath::isInRecordingFolder('Talk/Recording/g4qoq2me/sub/R.ogg', '/Talk/Recording', 'g4qoq2me'), false);
expect('another token', RecordingPath::isInRecordingFolder('Talk/Recording/g4qoq2me/R.ogg', '/Talk/Recording', 'oygp6e5d'), false);
expect('a similar prefix', RecordingPath::isInRecordingFolder('Talk/Recordings/g4qoq2me/R.ogg', '/Talk/Recording', 'g4qoq2me'), false);

echo $failures === 0 ? "ok - {$count} expectations\n" : "{$failures} of {$count} expectations failed\n";
exit($failures === 0 ? 0 : 1);
