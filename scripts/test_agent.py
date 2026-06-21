#!/usr/bin/env python3
"""Live end-to-end test of the AG-UI agent against database ground truth.

Builds a "knowledge base" of objectively-checkable facts straight from the DB for one
circuit/session, asks the agent a handful of questions over the live SSE endpoint, verifies
each answer against the facts, and confirms the stream actually streams (deltas over time).

This is the slow/opt-in tier: it needs a real OpenAI key, the running stack (aspire), and
imported data. For fast key-free tests of the agent plumbing, see
tests/RaceTelemetry.AgentApi.Tests (mocked LLM). Run:

    python scripts/test_agent.py                              # auto-pick a race with data
    python scripts/test_agent.py --session-id 2024-italian-grand-prix-r

Exit code is non-zero if any answer or streaming check fails, so it can gate iterations.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import uuid

DEFAULT_DATABASE_URL = "postgresql://race_telemetry:race_telemetry@localhost:5432/race_telemetry"
DEFAULT_AGENT_URL = "http://127.0.0.1:5124"


def database_url() -> str:
    return os.environ.get("RACE_TELEMETRY_DATABASE_URL", DEFAULT_DATABASE_URL)


# ---- Knowledge base: ground-truth facts from SQL ----

def pick_session(cur, session_id: str | None) -> str:
    if session_id:
        cur.execute("SELECT session_id FROM sessions WHERE session_id = %s", (session_id,))
        if cur.fetchone() is None:
            sys.exit(f"No session '{session_id}' in the database.")
        return session_id
    # Auto-pick the race with the most laps (most to ask about).
    cur.execute(
        """
        SELECT s.session_id
        FROM sessions s JOIN laps l USING (session_id)
        WHERE s.session_type = 'R'
        GROUP BY s.session_id
        ORDER BY count(*) DESC
        LIMIT 1
        """
    )
    row = cur.fetchone()
    if row is None:
        sys.exit("No race session with lap data found. Import one with scripts/import_session.py.")
    return row[0]


def build_kb(cur, sid: str) -> dict:
    """One value per fact, with a human label, so questions and checks can reference them."""
    facts: dict = {"session_id": sid}

    cur.execute("SELECT circuit_name, country FROM sessions WHERE session_id = %s", (sid,))
    facts["circuit_name"], facts["country"] = cur.fetchone()

    # "Took part" = drivers who actually set a lap (session_drivers can include reserves).
    cur.execute("SELECT count(DISTINCT driver_code), array_agg(DISTINCT driver_code) FROM laps WHERE session_id = %s", (sid,))
    facts["driver_count"], facts["driver_codes"] = cur.fetchone()

    cur.execute("SELECT max(lap_number) FROM laps WHERE session_id = %s", (sid,))
    facts["total_laps"] = cur.fetchone()[0]

    cur.execute(
        "SELECT driver_code, lap_time_ms FROM laps WHERE session_id = %s AND lap_time_ms IS NOT NULL "
        "ORDER BY lap_time_ms ASC LIMIT 1",
        (sid,),
    )
    facts["fastest_lap_driver"], facts["fastest_lap_ms"] = cur.fetchone()

    cur.execute(
        "SELECT driver_code, max(speed_kmh) FROM telemetry_samples WHERE session_id = %s AND speed_kmh IS NOT NULL "
        "GROUP BY driver_code ORDER BY max(speed_kmh) DESC LIMIT 1",
        (sid,),
    )
    facts["top_speed_driver"], facts["top_speed_kmh"] = cur.fetchone()

    # Deployments by kind (status codes 4=SC, 5=red, 6=VSC deployed) — kept separate to match
    # get_session_facts, which reports them individually.
    cur.execute(
        "SELECT count(*) FILTER (WHERE status_code = '4'), "
        "count(*) FILTER (WHERE status_code = '5'), "
        "count(*) FILTER (WHERE status_code = '6') "
        "FROM track_status_events WHERE session_id = %s",
        (sid,),
    )
    facts["safety_car_count"], facts["red_flag_count"], facts["vsc_count"] = cur.fetchone()

    cur.execute(
        "SELECT max(track_temp_c), bool_or(rainfall) FROM weather_samples WHERE session_id = %s",
        (sid,),
    )
    facts["peak_track_temp_c"], facts["rained"] = cur.fetchone()

    return facts


# ---- Questions tied to facts, with loose checkers (no LLM-judge) ----

def has_text(answer: str, expected) -> bool:
    return expected is not None and str(expected).lower() in answer.lower()


def has_number(answer: str, expected, tol_pct: float = 0.05) -> bool:
    if expected is None:
        return False
    nums = [float(n) for n in re.findall(r"\d+(?:\.\d+)?", answer.replace(",", ""))]
    target = float(expected)
    tol = max(abs(target) * tol_pct, 0.5)
    return any(abs(n - target) <= tol for n in nums)


def build_questions(kb: dict) -> list[dict]:
    return [
        {
            "q": "Which driver set the fastest lap of the session?",
            "check": lambda a: has_text(a, kb["fastest_lap_driver"]),
            "expect": f"fastest lap by {kb['fastest_lap_driver']}",
        },
        {
            "q": "What was the highest top speed recorded and which driver hit it?",
            "check": lambda a: has_text(a, kb["top_speed_driver"]) and has_number(a, kb["top_speed_kmh"]),
            "expect": f"{kb['top_speed_driver']} at ~{kb['top_speed_kmh']} km/h",
        },
        {
            "q": "How many drivers took part in this session?",
            "check": lambda a: has_number(a, kb["driver_count"], tol_pct=0),
            "expect": f"{kb['driver_count']} drivers",
        },
        {
            # The agent may give the total (4) or the breakdown (3 SC + 1 red); accept either.
            "q": "How many times was a safety car or red flag deployed?",
            "check": lambda a: has_number(a, kb["safety_car_count"] + kb["red_flag_count"], tol_pct=0)
            or (has_number(a, kb["safety_car_count"], tol_pct=0) and has_number(a, kb["red_flag_count"], tol_pct=0)),
            "expect": f"{kb['safety_car_count']} SC + {kb['red_flag_count']} red",
        },
        {
            "q": "What was the peak track temperature?",
            "check": lambda a: has_number(a, kb["peak_track_temp_c"]),
            "expect": f"~{kb['peak_track_temp_c']} C",
        },
        {
            # circuit_name and country are synonyms here (e.g. "Monte Carlo" / "Monaco").
            "q": "What circuit was this session held at?",
            "check": lambda a: has_text(a, kb["circuit_name"]) or has_text(a, kb["country"]),
            "expect": f"{kb['circuit_name']} / {kb['country']}",
        },
    ]


# ---- Live SSE call ----

def ask(agent_url: str, session_id: str, question: str, timeout: int = 120) -> dict:
    """POST one question, parse the SSE stream. Returns answer text, tools, delta timing."""
    body = json.dumps({
        "threadId": str(uuid.uuid4()),
        "messages": [{"id": "1", "role": "user", "content": question}],
        "state": {"sessionKey": session_id},
    }).encode()
    req = urllib.request.Request(
        f"{agent_url}/ag-ui",
        data=body,
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
        method="POST",
    )
    answer, tools, deltas, error = [], [], [], None
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        for raw in resp:  # iterate as bytes arrive
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data: "):
                continue
            evt = json.loads(line[6:])
            t = evt.get("type")
            if t == "TOOL_CALL_START":
                tools.append(evt.get("toolCallName"))
            elif t == "TEXT_MESSAGE_CONTENT":
                answer.append(evt.get("delta", ""))
                deltas.append(time.monotonic())
            elif t == "RUN_ERROR":
                error = f"{evt.get('code')}: {evt.get('message')}"
            elif t == "RUN_FINISHED":
                break
    return {
        "answer": "".join(answer),
        "tools": tools,
        "delta_count": len(deltas),
        "stream_span_ms": (deltas[-1] - deltas[0]) * 1000 if len(deltas) >= 2 else 0.0,
        "error": error,
    }


def health_ok(agent_url: str) -> bool:
    try:
        with urllib.request.urlopen(f"{agent_url}/health/ready", timeout=10) as r:
            return r.status == 200
    except urllib.error.URLError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session-id", help="Session to test (default: auto-pick a race with most laps)")
    parser.add_argument("--database-url", default=database_url())
    parser.add_argument("--agent-url", default=DEFAULT_AGENT_URL)
    args = parser.parse_args()

    if not health_ok(args.agent_url):
        return _fail(f"Agent not ready at {args.agent_url}/health/ready. Start the stack (aspire) "
                     "with an OpenAI key and imported data.")

    import psycopg  # same driver as the rest of scripts/
    with psycopg.connect(args.database_url) as conn, conn.cursor() as cur:
        sid = pick_session(cur, args.session_id)
        kb = build_kb(cur, sid)

    kb_path = os.path.join(os.path.dirname(__file__), "..", "tests", f"agent_kb_{sid}.json")
    with open(kb_path, "w") as f:
        json.dump(kb, f, indent=2, default=str)
    print(f"Session: {sid}  ({kb['circuit_name']}, {kb['country']})")
    print(f"Knowledge base -> {os.path.relpath(kb_path)}\n")

    questions = build_questions(kb)
    failures = 0
    max_deltas = 0  # streaming is a pipeline property: proven if any answer arrives incrementally
    for item in questions:
        result = ask(args.agent_url, sid, item["q"])
        if result["error"]:
            ok = False
            note = f"RUN_ERROR {result['error']}"
        else:
            ok = item["check"](result["answer"])
            note = f"expect {item['expect']}"
        if not ok:
            failures += 1
        max_deltas = max(max_deltas, result["delta_count"])
        # A short answer may legitimately arrive in one chunk; that's the model's choice, not a
        # broken stream — so per-question delta count is reported, not scored.
        print(f"[{'PASS' if ok else 'FAIL'}] {item['q']}")
        print(f"       tools: {result['tools'] or '-'}  |  {note}")
        print(f"       stream: {result['delta_count']} deltas over {result['stream_span_ms']:.0f}ms")
        print(f"       answer: {result['answer'][:160]}\n")

    total = len(questions)
    streaming_ok = max_deltas >= 2  # at least one answer streamed incrementally over the SSE pipeline
    print(f"Results: {total - failures}/{total} answers correct")
    print(f"Streaming: {'WORKS' if streaming_ok else 'NOT OBSERVED'} (max {max_deltas} deltas in one answer)")
    return 1 if failures or not streaming_ok else 0


def _fail(msg: str) -> int:
    print(msg, file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
