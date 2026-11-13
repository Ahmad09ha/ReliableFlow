#!/usr/bin/env python3
"""Actual SIGKILL test: crash relay AFTER a broker confirm, BEFORE outbox update.

Run after docker compose up -d --build; this test does not require a fresh DB.
Requires local-only dev fault routes; it deliberately restarts the ordering container.
"""
import json
import os
import subprocess
import time
import urllib.error
import urllib.request

ORDERING = os.environ.get("RF_ORDERING_URL", "http://127.0.0.1:8080")
INVENTORY = os.environ.get("RF_INVENTORY_URL", "http://127.0.0.1:8081")


def call(base, path, method="GET", data=None):
    request = urllib.request.Request(
        base + path,
        data=None if data is None else json.dumps(data).encode(),
        method=method,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.loads(response.read().decode())


def until(label, condition, seconds=125):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            if condition():
                print(f"PASS: {label}")
                return
        except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError) as error:
            last = error
        time.sleep(0.5)
    raise AssertionError(f"TIMEOUT: {label} ({last})")


def stock():
    return next(i["available"] for i in call(INVENTORY, "/stock") if i["sku"] == "BOOK-001")


def reservation(order_id):
    return [i for i in call(INVENTORY, "/reservations") if i["orderId"] == order_id]


def outbox(event_id):
    return next(i for i in call(ORDERING, "/outbox") if i["id"] == event_id)


def duplicates():
    return call(INVENTORY, "/demo/consumer-metrics")["duplicates"]


def docker(*args):
    subprocess.run(["docker", "compose", *args], check=True, timeout=90)


def main():
    until("both demo APIs healthy", lambda:
          call(ORDERING, "/health")["status"] == "ok"
          and call(INVENTORY, "/health")["status"] == "ok")
    before_stock = stock()
    before_duplicates = duplicates()

    call(ORDERING, "/demo/faults/pause-after-publish-once", "POST")
    order = call(ORDERING, "/orders", "POST",
                 {"sku": "BOOK-001", "quantity": 1})
    order_id, event_id = order["id"], order["eventId"]

    # IsPaused is set only AFTER BasicPublishAsync returns with publisher confirm.
    until("publisher reached broker-confirmed pause", lambda:
          call(ORDERING, "/demo/faults/status")["isPausedAfterConfirm"])
    until("consumer applied original delivery", lambda: len(reservation(order_id)) == 1)
    assert stock() == before_stock - 1
    assert outbox(event_id)["publishedAtUtc"] is None, "Outbox was marked before SIGKILL"

    docker("kill", "-s", "SIGKILL", "ordering")
    print("PASS: actual SIGKILL of ordering container at confirmed-publish boundary")
    docker("up", "-d", "ordering")

    until("ordering process restarted", lambda: call(ORDERING, "/health")["status"] == "ok")
    # The relay cannot reclaim before its old 40s lease expires.
    until("expired lease reclaimed and event re-published", lambda:
          outbox(event_id)["publishedAtUtc"] is not None, seconds=145)
    until("duplicate delivery detected by receiver", lambda:
          duplicates() > before_duplicates, seconds=50)

    assert len(reservation(order_id)) == 1, "Unexpected duplicate reservation"
    assert stock() == before_stock - 1, "Duplicate delivery changed stock twice"
    assert len([i for i in call(INVENTORY, "/inbox") if i["messageId"] == event_id]) == 1
    print("PASS: hard SIGKILL -> lease expiry -> duplicate publish -> one business effect")


if __name__ == "__main__":
    main()
