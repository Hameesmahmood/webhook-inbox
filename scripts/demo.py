#!/usr/bin/env python3
"""A 30-second demo: send 1,000 webhooks, kill the worker with SIGKILL partway through delivery,
start a new one, and check that every acknowledged event reached the consumer.

Usage: docker compose up --build --wait, then python3 scripts/demo.py

Uses the stack from compose.yaml (one worker, 30-second leases) and only the standard library.
Exits with 1 if anything acknowledged was lost.
"""
import json
import subprocess
import sys
import threading
import time
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor

API = "http://localhost:8080"
SINK = "http://localhost:8081"
EVENTS = 1000
# Kill while webhooks are still arriving and deliveries are under way.
KILL_WHEN_DELIVERED = 100
KILL_WHEN_ACKNOWLEDGED = 400


def request(method, url, body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json", **(headers or {})})
    with urllib.request.urlopen(req, timeout=10) as response:
        text = response.read().decode()
        return response.status, (json.loads(text) if text else None)


def delivered(ids):
    _, body = request("GET", f"{SINK}/received")
    accepted = body["accepted"]
    return sum(1 for i in ids if i in accepted), accepted


def compose(*args):
    subprocess.run(["docker", "compose", *args], check=True, capture_output=True)


def say(text=""):
    print(text, flush=True)


run = uuid.uuid4().hex[:6]
request("POST", f"{SINK}/reset")
say("1. Stack up: the API, one worker, Postgres and a demo consumer.")

ids, acknowledged = [], 0
lock = threading.Lock()


def send(n):
    global acknowledged
    status, body = request("POST", f"{API}/v1/hooks/demo", {"type": "order.paid", "order": n}, {"X-Event-Id": f"{run}-{n}"})
    with lock:
        if status == 202:
            acknowledged += 1
            ids.append(body["id"])


say(f"\n2. Sending {EVENTS:,} webhooks. Each gets a 202 once it's saved.")
started = time.monotonic()
pool = ThreadPoolExecutor(max_workers=24)
futures = [pool.submit(send, n) for n in range(1, EVENTS + 1)]

while True:
    with lock:
        snapshot, sent = list(ids), acknowledged
    count, _ = delivered(snapshot)
    if count >= KILL_WHEN_DELIVERED or sent >= KILL_WHEN_ACKNOWLEDGED:
        break
    time.sleep(0.05)

compose("kill", "-s", "SIGKILL", "worker")
with lock:
    sent_at_kill = acknowledged
at_kill, _ = delivered(ids)
say("\n3. Worker killed with SIGKILL mid-delivery. No chance to clean up.")
say(f"   By then: {sent_at_kill:,} acknowledged, {at_kill:,} delivered.")

for f in futures:
    f.result()
pool.shutdown()
before, _ = delivered(ids)
time.sleep(3)
after, _ = delivered(ids)
say("")
if acknowledged > sent_at_kill:
    say(f"4. The API kept accepting: {acknowledged - sent_at_kill:,} more acknowledged after the kill.")
else:
    say(f"4. All {acknowledged:,} were acknowledged.")
if before == after:
    say(f"   Delivered stays at {after:,}. The other {acknowledged - after:,} wait in Postgres.")
else:
    say(f"   Delivered: {after:,}. The other {acknowledged - after:,} wait in Postgres.")

compose("up", "-d", "worker")
say("\n5. New worker started. Anything the dead worker held comes back")
say("   when its 30-second lease runs out.")
deadline = time.monotonic() + 120
shown, changes = None, []
while True:
    count, accepted = delivered(ids)
    # Print only on change, so the wait for the lease is silence (and a recording can shorten it).
    if count != shown:
        sys.stdout.write(f"\r   Delivered: {count:,} of {acknowledged:,}")
        sys.stdout.flush()
        shown = count
        changes.append((time.monotonic(), count))
    if count == acknowledged or time.monotonic() > deadline:
        break
    time.sleep(0.5)
say()
# A pause of 20 s or more before the last deliveries means they were held by the dead worker.
waits = [(changes[i][0] - changes[i - 1][0], changes[i - 1][1]) for i in range(1, len(changes))]
held = next((acknowledged - before for gap, before in waits if gap >= 20), 0)
if held:
    say(f"   The last {held} were held by the dead worker; they waited for its lease.")
else:
    say("   The dead worker held none when it died, so nothing waited for a lease.")

lost = [i for i in ids if i not in accepted]
twice = sum(accepted[i] - 1 for i in ids if i in accepted)
say(f"\n6. {acknowledged:,} acknowledged, {count:,} delivered, {len(lost)} lost.")
if twice:
    say(f"   {twice} delivered twice, same Webhook-Id, so the consumer drops them.")
else:
    say("   None delivered twice this time.")
sys.exit(1 if lost else 0)
