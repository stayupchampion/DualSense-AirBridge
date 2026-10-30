"""Summarize software timing evidence; never infer physical latency from it."""
import argparse
import json
import math
import re
from datetime import datetime
from pathlib import Path


def distribution(values):
    items = sorted(values)
    def percentile(p):
        return items[math.ceil((len(items) - 1) * p)] if items else None
    return dict(count=len(items), min=items[0] if items else None,
                median=percentile(.5), p95=percentile(.95), max=percentile(1))


def capture_summary(path):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    ahead = [c["FramesAhead"] * 1000 / c["SampleRate"] for c in data["Commands"]
             if c.get("FramesAhead") is not None and c.get("SampleRate")]
    return dict(file=str(path.resolve()), mode=data["Mode"], status=data["Status"],
                version=data.get("AppVersion", "0.1.2 or earlier; no embedded version"),
                started=data["Started"], finished=data["Finished"],
                submitted_onsets=len(data["Commands"]), samples_ahead_ms=distribution(ahead),
                report_arrival_ms=data["ReportArrivalIntervals"], audio=data["AudioDetails"],
                physical_latency_ms=None)


def backend_summary(path, start=None, end=None):
    summaries, writers = [], []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if "audio summary interval_us=" not in line and "hid output writer queued=" not in line:
            continue
        time = datetime.fromisoformat(line.split("|", 1)[0])
        if start and time < start or end and time > end:
            continue
        values = {key: int(value) for key, value in re.findall(r"(\w+)=(-?\d+)", line)}
        (summaries if "audio summary interval_us=" in line else writers).append(values)
    return dict(file=str(path.resolve()), summary_samples=len(summaries),
                sampled_queue_wait_ms=distribution([x["queue_age_last_us"] / 1000 for x in summaries]),
                reported_max_queue_wait_ms=max((x["queue_age_max_us"] / 1000 for x in summaries), default=None),
                reported_max_usb_gap_ms=max((x["usb_max_gap_us"] / 1000 for x in summaries), default=None),
                reported_max_send_gap_ms=max((x["send_max_gap_us"] / 1000 for x in summaries), default=None),
                summary_interval_drops=sum(x["dropped_delta"] for x in summaries),
                summary_interval_concealment=sum(x["concealed_delta"] for x in summaries),
                writer_pending_samples=distribution([x["pending"] for x in writers]),
                limits="Queue samples are one per summary, not a per-packet distribution. Max counters may include activity before the requested window. Boundary intervals can overlap activity outside that window. Bluetooth write completion is not actuator onset.")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--capture", type=Path, action="append", default=[])
    p.add_argument("--backend", type=Path)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    result = dict(physical_latency_ms=None, captures=[capture_summary(f) for f in args.capture])
    if args.backend:
        # A single capture limits summary timestamps. Multiple captures compare
        # client buffers and leave backend aggregation over the full session.
        only = result["captures"][0] if len(result["captures"]) == 1 else None
        result["backend"] = backend_summary(args.backend,
            datetime.fromisoformat(only["started"]) if only else None,
            datetime.fromisoformat(only["finished"]) if only else None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(args.output.resolve())


if __name__ == "__main__":
    main()
