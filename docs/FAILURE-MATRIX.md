# Failure & Recovery Matrix

| Scenario | State after failure | Recovery | Expected user-visible result |
| --- | --- | --- | --- |
| API fails before `SaveChanges` | Nothing committed | Client retries API | No phantom event |
| DB insert fails | Order and event roll back | Caller handles DB error | No partial successful order |
| API crashes after DB commit, before HTTP response | Order + outbox committed; client unaware | Client needs HTTP idempotency key (not built into demo) | Potential duplicate *orders* if request retried; outbox does not solve HTTP request deduplication |
| Broker offline | Outbox remains pending; error + next retry stored | Restore broker / publisher | Event eventually arrives |
| Broker confirms publish, relay crashes before marker | Broker has event; outbox still pending | Lease expires, publish again | Same stable `MessageId`, consumer effect once |
| Two dispatchers race to claim same event | Conditional update arbitrates lease | Loser skips | One active owner per unexpired lease |
| One dispatcher hangs longer than lease | Other dispatcher may claim and send | Consumer inbox dedup | Possible duplicate event, not duplicate transactional effects |
| Consumer fails before commit | Marker & stock rollback | RabbitMQ redelivery | No lost/reserved partial stock |
| Consumer commits, ACK fails | Stock + marker committed, delivery may repeat | Inbox detects duplicate, ACK | One stock reservation |
| Two consumers concurrently receive same ID | Unique DB constraint decides | One commits; other returns Duplicate | One transactional effect |
| Different IDs represent same order | Business-key `OrderId` uniqueness protects reservation | Same order ignored or conflict sent for review | No double stock decrement |
| Poison payload cannot be parsed | No business change | Reject to DLQ | Event requires operator investigation |
| Temporary DB failure persists | Message may exceed quorum delivery limit | DLQ investigation/replay policy | No false success |
| External payment API called inside handler | Outside database transaction | Needs payment-provider idempotency key / saga | **Not covered by library** |
| Event for same aggregate arrives out of order | No ordering guarantee | Add versions / sequence check | **Not covered by library** |

## Things deliberately not claimed

"Exactly-once delivery" is not guaranteed. "All business effects in any external system are applied exactly once" is not guaranteed. The demonstration only shows atomic **local** changes across an inbox marker and a domain database, coupled with at-least-once message delivery.
