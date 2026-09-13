#!/bin/sh
# Launcher for the self-contained build in /app/lib/rgdscapture.
#
# The FFmpeg built by this manifest installs to /app/lib, which is not on the
# loader's default path inside the runtime. FFmpeg.AutoGen is pointed at it
# explicitly by Services/FFmpegLoader.cs, but the ffmpeg binary itself also
# has to find its own libraries, so the path is exported here for both.
export LD_LIBRARY_PATH="/app/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

exec /app/lib/rgdscapture/rgdscapture "$@"
