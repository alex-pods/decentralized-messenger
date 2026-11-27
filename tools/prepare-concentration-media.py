"""Prepare the explicitly selected training clips; publish output separately from source code."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess

SOURCES = [("HIReuFwB4-g", [15]), ("2b6Y-GcU_j8", [33]), ("2zCg0xDT48A", [54]),
           ("fFrQMSZZJ1M", [21, 73]), ("Oc5bIgsg1Iw", [16]), ("mO9iWE499A0", [65]), ("OE8ejGcUAt0", [55])]
# Inspected over every 36 s segment with cropdetect and contact sheets, in a 1280x720 reference.
# Tiny changing edges in edits are covered by a small inward margin; dark artwork is not a black bar.
CROPS = {"HIReuFwB4-g": "1268:716:6:2", "2b6Y-GcU_j8": "1280:720:0:0",
         "2zCg0xDT48A": "1276:516:2:102", "fFrQMSZZJ1M": "1268:716:6:2",
         "Oc5bIgsg1Iw": "1240:708:20:6", "mO9iWE499A0": "1268:716:6:2",
         # The white cover in this source is centered at x=623, not at the canvas center x=640.
         "OE8ejGcUAt0": "1246:720:0:0"}

def prepare(video, target, output):
    stem = f"{video}-{target:03}"
    final = output / (stem + ".mp4")
    log = output / (stem + ".log")
    with log.open("w", encoding="utf-8") as stream:
        if not final.exists():
            raw = output / (stem + ".source.%(ext)s")
            subprocess.run(["yt-dlp", "--js-runtimes", "node", "--no-playlist", "--no-warnings", "--no-progress",
                "-f", "bv*[height<=720]+ba/b[height<=720]",
                "--merge-output-format", "mkv", "-o", str(raw), f"https://youtu.be/{video}"],
                stdout=stream, stderr=stream, check=True)
            source = next(p for p in output.glob(stem + ".source.*") if p.suffix in (".mkv", ".webm", ".mp4"))
            # Decode from the source timestamp, rather than accepting a preceding GOP boundary.
            subprocess.run(["ffmpeg", "-nostdin", "-hide_banner", "-y", "-ss", str(target-4), "-i", str(source), "-t", "36",
                "-vf", "scale=1280:720,crop="+CROPS[video]+",scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,setsar=1,fps=30",
                "-c:v", "libx264", "-preset", "fast", "-crf", "24", "-maxrate", "1800k", "-bufsize", "3600k",
                "-pix_fmt", "yuv420p", "-g", "30", "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
                "-movflags", "+faststart", str(final)], stdout=stream, stderr=stream, check=True)
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-show_format", "-show_streams", "-of", "json", str(final)]))
    duration = float(probe["format"]["duration"])
    assert 35.5 <= duration <= 36.5, (stem, duration)
    assert any(s["codec_type"] == "audio" for s in probe["streams"]), stem
    # Area-average every frame, then average over the complete clip (not just its first frame).
    rgb = subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(final), "-an",
        "-vf", "scale=1:1:flags=area,format=rgb24", "-f", "rawvideo", "-"])
    average = "#" + "".join(f"{round(sum(rgb[channel::3])/len(rgb[channel::3])):02X}" for channel in range(3))
    print(f"READY {stem}: {final.stat().st_size/1024/1024:.1f} MiB, {duration:.2f}s", flush=True)
    return {"id": stem, "videoId": video, "sourceStartSeconds": target-4, "targetStartSeconds": target,
            "leadInMs": 4000, "durationMs": 36000, "bytes": final.stat().st_size,
            "sha256": hashlib.sha256(final.read_bytes()).hexdigest(), "file": final.name, "crop": CROPS[video], "fit": "cover",
            "averageColor": average}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args(); args.out.mkdir(parents=True, exist_ok=True)
    jobs = [(video, t) for video, times in SOURCES for t in times]
    with ThreadPoolExecutor(max_workers=2) as pool:
        clips = list(pool.map(lambda p: prepare(*p, args.out), jobs))
    (args.out / "manifest.json").write_text(json.dumps({"version": 1, "clips": clips}, indent=2), encoding="utf-8")
