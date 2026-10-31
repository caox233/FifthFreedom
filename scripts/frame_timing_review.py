"""Offline numeric frame comparison; never launches, attaches to or captures a game.

Input is an explicitly prepared JSON projection, not a game log. Quantiles use
nearest rank. Sub-view timings, draw-error counts and CPU/GPU timings cannot be
silently substituted for one another. No raw paths, players or free-form labels
are accepted or copied to the report.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys

MAX_BYTES = 8 * 1024 * 1024
MAX_FRAMES = 100_000
METRICS = {"present_interval_ms", "cpu_frame_ms", "gpu_frame_ms"}
CONTROLS = {"same_machine", "same_scene", "same_graphics_settings", "same_capture_method"}
GROUPS = (("baseline", "off"), ("baseline", "on"), ("modified", "off"), ("modified", "on"))


class InputError(ValueError):
    """Only fixed error codes are exposed; input values may be private."""


def exact(value, keys, code):
    if type(value) is not dict or set(value) != set(keys):
        raise InputError(code)


def integer(value, low, high):
    return type(value) is int and low <= value <= high


def summary(values):
    if not values:
        return {"count": 0, "p50_ms": None, "p95_ms": None, "p99_ms": None,
                "max_ms": None, "at_least_100_frames": False}
    ordered = sorted(values)
    def rank(percent):
        return ordered[math.ceil(len(ordered) * percent / 100) - 1]
    return {"count": len(ordered), "p50_ms": rank(50), "p95_ms": rank(95),
            "p99_ms": rank(99), "max_ms": ordered[-1],
            "at_least_100_frames": len(ordered) >= 100}


def validate(data):
    exact(data, {"schema", "evidence", "metric", "controls", "runs"}, "DOCUMENT_FIELDS")
    if type(data["schema"]) is not int or data["schema"] != 1:
        raise InputError("SCHEMA")
    if data["evidence"] not in ("synthetic_fixture", "reviewed_frame_timing"):
        raise InputError("EVIDENCE_KIND")
    if not isinstance(data["metric"], str) or data["metric"] not in METRICS:
        raise InputError("FRAME_METRIC")
    exact(data["controls"], CONTROLS, "CONTROL_FIELDS")
    if any(type(value) is not bool for value in data["controls"].values()):
        raise InputError("CONTROL_VALUES")
    runs = data["runs"]
    if type(runs) is not list or not 4 <= len(runs) <= 40:
        raise InputError("RUN_COUNT")
    identities, builds, groups = set(), {}, {group: set() for group in GROUPS}
    total = 0
    for run in runs:
        exact(run, {"phase", "emf", "sample", "build_sha", "frames"}, "RUN_FIELDS")
        if run["phase"] not in ("baseline", "modified") or run["emf"] not in ("off", "on"):
            raise InputError("RUN_GROUP")
        if not integer(run["sample"], 1, 10):
            raise InputError("SAMPLE_ID")
        identity = (run["phase"], run["emf"], run["sample"])
        if identity in identities:
            raise InputError("DUPLICATE_RUN")
        identities.add(identity)
        groups[identity[:2]].add(run["sample"])
        sha = run["build_sha"]
        if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise InputError("BUILD_SHA")
        if builds.setdefault(run["phase"], sha) != sha:
            raise InputError("MIXED_BUILD_IN_PHASE")
        frames = run["frames"]
        if type(frames) is not list or not 2 <= len(frames) <= MAX_FRAMES:
            raise InputError("FRAME_COUNT")
        total += len(frames)
        if total > MAX_FRAMES:
            raise InputError("FRAME_LIMIT")
        for index, frame in enumerate(frames):
            exact(frame, {"index", "frame_ms", "width", "height"}, "FRAME_FIELDS")
            if type(frame["index"]) is not int or frame["index"] != index:
                raise InputError("FRAME_SEQUENCE")
            ms = frame["frame_ms"]
            if type(ms) not in (int, float) or not 0 < ms <= 60_000 or not math.isfinite(ms):
                raise InputError("FRAME_DURATION")
            if not all(integer(frame[key], 0, 16_384) for key in ("width", "height")):
                raise InputError("FRAME_DIMENSIONS")
    sample_sets = list(groups.values())
    if not sample_sets[0] or any(samples != sample_sets[0] for samples in sample_sets):
        raise InputError("UNPAIRED_GROUPS")
    return runs


def analyze(data):
    runs = validate(data)
    results = []
    for run in sorted(runs, key=lambda row: (row["sample"], row["phase"], row["emf"])):
        visible = [frame["frame_ms"] for frame in run["frames"] if frame["width"] > 0 and frame["height"] > 0]
        zero = [frame["frame_ms"] for frame in run["frames"] if frame["width"] == 0 or frame["height"] == 0]
        results.append({key: run[key] for key in ("phase", "emf", "sample", "build_sha")} | {
            "all_frames": summary([frame["frame_ms"] for frame in run["frames"]]),
            "positive_dimensions": summary(visible), "zero_dimension_frames": summary(zero),
            "positive_resolutions": sorted({(frame["width"], frame["height"]) for frame in run["frames"]
                                            if frame["width"] > 0 and frame["height"] > 0}),
        })
    comparisons = []
    controls_match = all(data["controls"].values())
    for sample in sorted({row["sample"] for row in results}):
        quartet = {(row["phase"], row["emf"]): row for row in results if row["sample"] == sample}
        for population in ("all_frames", "positive_dimensions"):
            values = {group: quartet[group][population] for group in GROUPS}
            enough = all(value["at_least_100_frames"] for value in values.values())
            # All-frame summaries retain zero-size periods. A visible-only
            # comparison must never conceal their presence or count difference.
            zero_counts = [quartet[group]["zero_dimension_frames"]["count"] for group in GROUPS]
            resolutions = [quartet[group]["positive_resolutions"] for group in GROUPS]
            dimensions_match = len(resolutions[0]) == 1 and all(value == resolutions[0] for value in resolutions)
            blocked = []
            if not controls_match:
                blocked.append("CONTROL_MISMATCH")
            if not dimensions_match:
                blocked.append("RESOLUTION_MISMATCH_OR_UNAVAILABLE")
            if not enough:
                blocked.append("FEWER_THAN_100_FRAMES_IN_A_CELL")
            paired = {"sample": sample, "population": population,
                      "controls_match": controls_match, "at_least_100_frames_each": enough,
                      "positive_resolution_matches": dimensions_match,
                      "comparison_status": "blocked" if blocked else "descriptive_only",
                      "blocked_reasons": blocked,
                      "zero_dimension_counts_in_group_order": zero_counts,
                      "group_order": ["/".join(group) for group in GROUPS]}
            for percentile in ("p95_ms", "p99_ms"):
                if blocked or any(value[percentile] is None for value in values.values()):
                    paired[percentile] = None
                    continue
                b_off, b_on, m_off, m_on = (values[group][percentile] for group in GROUPS)
                paired[percentile] = {
                    "baseline_on_minus_off": b_on - b_off,
                    "modified_on_minus_off": m_on - m_off,
                    "modified_minus_baseline_off": m_off - b_off,
                    "modified_minus_baseline_on": m_on - b_on,
                    "difference_of_toggle_differences": (m_on - m_off) - (b_on - b_off),
                }
            comparisons.append(paired)
    return {"schema": 1, "evidence": data["evidence"], "metric": data["metric"],
            "controls": data["controls"], "percentile_method": "nearest_rank",
            "quantile_differences_are_not_paired_frame_or_causal_estimates": True,
            "input_provenance_independently_verified": False,
            "game_started": False, "capture_started": False,
            "performance_fix_verified": False, "runs": results, "comparisons": comparisons}


def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise InputError("DUPLICATE_JSON_KEY")
        result[key] = value
    return result


def read_input(path):
    try:
        with path.open("rb") as stream:
            raw = stream.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise InputError("INPUT_SIZE")
        data = json.loads(raw.decode("utf-8"), object_pairs_hook=no_duplicate_keys)
    except (OSError, UnicodeError, json.JSONDecodeError, RecursionError):
        raise InputError("INPUT_READ_OR_JSON") from None
    return data, hashlib.sha256(raw).hexdigest()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", required=True, type=Path, help="New local report; existing files are never overwritten")
    args = parser.parse_args(argv)
    try:
        data, digest = read_input(args.input)
        report = analyze(data) | {"input_sha256": digest}
        with args.output.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, ensure_ascii=True, indent=2, allow_nan=False)
            stream.write("\n")
    except InputError as error:
        print(str(error), file=sys.stderr)
        return 1
    except OSError:
        print("OUTPUT_CREATE", file=sys.stderr)
        return 1
    print("FRAME_REVIEW_WRITTEN performance_fix_verified=false")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
