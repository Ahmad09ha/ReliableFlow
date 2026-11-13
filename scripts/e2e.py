#!/usr/bin/env python3
"""Dependency-free Docker Compose E2E exercise. Assumes fresh demo databases."""
import json
import os
import time
import urllib.error
import urllib.request

ORDERING = os.environ.get("RF_ORDERING_URL", "http://127.0.0.1:8080")
INVENTORY = os.environ.get("RF_INVENTORY_URL", "http://127.0.0.1:8081")


def call(base, path, method="GET", payload=None):
    body = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(
        base + path,
        data=body,
        headers={"Content-Type": "application/json"},
        method=method,
    )
    with urllib.request.urlopen(request, timeout=8) as response:
        content = response.read().decode()
        return json.loads(content) if content else None


def until(label, test, seconds=100):
    end = time.monotonic() + seconds
    last_error = ""
    while time.monotonic() < end:
        try:
            if test():
                print(f"PASS: {label}")
                return
        except (urllib.error.URLError, ConnectionError, TimeoutError, ValueError) as error:
            last_error = str(error)
        time.sleep(0.75)
    raise AssertionError(f"TIMEOUT: {label} {last_error}")


def available():
    stocks = call(INVENTORY, "/stock")
    return next(item["available"] for item in stocks if item["sku"] == "BOOK-001")


def reservation(order_id):
    return [x for x in call(INVENTORY, "/reservations") if x["orderId"] == order_id]


def outbox_message(event_id):
    return next(x for x in call(ORDERING, "/outbox") if x["id"] == event_id)

def duplicate_deliveries():
    return call(INVENTORY, "/demo/consumer-metrics")["duplicates"]


def new_order(quantity):
    return call(ORDERING, "/orders", "POST", {"sku": "BOOK-001", "quantity": quantity})


if __name__ == "__main__":
    until("ordering API ready", lambda: call(ORDERING, "/health")["status"] == "ok")
    until("inventory API ready", lambda: call(INVENTORY, "/health")["status"] == "ok")
    assert available() == 100, "Run E2E against fresh demo DB (docker compose down -v)"

    # Happy path: SaveChanges commits the Order + outbox entry, then consumer reserves stock.
    first = new_order(2)
    until("happy-path reservation", lambda: len(reservation(first["id"])) == 1)
    until("happy-path outbox published", lambda: outbox_message(first["eventId"])["publishedAtUtc"] is not None)
    assert available() == 98, "Expected one stock decrement after first event"

    # Broker outage: the order is committed while its event remains pending.
    call(ORDERING, "/demo/faults/publisher/down", "POST")
    second = new_order(1)
    until("broker-down retry recorded", lambda: outbox_message(second["eventId"])["attemptCount"] >= 1)
    assert len(reservation(second["id"])) == 0
    assert available() == 98
    call(ORDERING, "/demo/faults/publisher/up", "POST")
    until("recovered publisher dispatch", lambda: outbox_message(second["eventId"])["publishedAtUtc"] is not None)
    until("recovered consumer reservation", lambda: len(reservation(second["id"])) == 1)
    assert available() == 97

    # Broker CONFIRMED, then throws before marking the outbox row published.
    duplicates_before = duplicate_deliveries()
    call(ORDERING, "/demo/faults/fail-after-publish-once", "POST")
    third = new_order(3)
    until("post-publish failure was observed", lambda: outbox_message(third["eventId"])["attemptCount"] >= 1)
    until("post-publish retry eventually succeeded", lambda: outbox_message(third["eventId"])["publishedAtUtc"] is not None)
    until("third order was reserved", lambda: len(reservation(third["id"])) == 1)
    until("re-published event detected as duplicate", lambda: duplicate_deliveries() > duplicates_before)
    assert available() == 94, "Duplicate delivery must not reserve 6 units"

    # One processed marker for the third event despite multiple publish attempts.
    inbox = call(INVENTORY, "/inbox")
    assert len([x for x in inbox if x["messageId"] == third["eventId"]]) == 1
    assert len(reservation(third["id"])) == 1
    print("PASS: outbox + inbox E2E failure lab")
