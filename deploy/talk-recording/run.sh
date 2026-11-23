#!/bin/sh
# talk-recording's command: the image's /start.sh has written
# /conf/recording.conf by now and execs this in its place.
#
# TALK_RECORDING_AUDIO_ONLY=true makes every recording audio only. Talk's web
# client always asks for a video recording (status 1, no choice offered) though
# its API and this server can do audio only (status 2), so the video settings
# are rewritten instead: the screen is still grabbed (at 1 frame a second) but
# not encoded (-vn). The audio is always Opus in .ogg (audio/ogg, which Talk
# accepts): where the image found a hardware encoder it sets AAC in .m4a,
# which Talk's upload refuses. The browser still renders the call; the saving
# is the encode and the file (an hour of 1080p video is ~400 MB, of Opus audio
# at most ~45 MB).
set -eu
conf=/conf/recording.conf

if [ "${TALK_RECORDING_AUDIO_ONLY:-false}" = true ]; then
    awk '
        /^(inputvideo|outputvideo|extensionvideo|outputaudio|extensionaudio)[[:space:]]*=/ { next }
        { print }
        /^\[ffmpeg\]$/ {
            print "inputvideo = -framerate 1"
            print "outputvideo = -vn"
            print "outputaudio = -c:a libopus"
            print "extensionvideo = .ogg"
            print "extensionaudio = .ogg"
        }' "$conf" > "$conf.new"
    mv "$conf.new" "$conf"
    echo "Audio-only recordings (Opus, .ogg)"
fi

exec "$@"
