# ReliableFlow — شرح من الصفر بمثال عملي

> هذا الملف مخصص للتعلّم. الـ README الرئيسي بالإنكليزية، والمكتبة ليست بديلاً عن تصميم الـ Business Logic أو نظاماً يضمن Exactly-Once Delivery لكل الأنظمة الخارجية.

## 1) شو المشكلة التي نحلها؟

عنا تطبيق متجر يتكوّن من خدمتين:

- **Ordering API**: تحفظ طلب المستخدم ضمن قاعدة بيانات `orders_db`.
- **Inventory Service**: تحجز المنتجات ضمن قاعدة بيانات أخرى اسمها `inventory_db`.

الرسالة تنتقل بينهما عن طريق **RabbitMQ**. الـ RabbitMQ ليس قاعدة بيانات الطلبات؛ هو Message Broker يستقبل الرسائل ويجعلها متاحة للمستهلكين (Consumers).

للتبسيط، تخيّل عندنا 10 كتب، والمستخدم طلب 3 كتب. المفروض بعد استقبال حدث الطلب يصير المتاح 7.

## 2) الكود الساذج وأين يفشل

```csharp
db.Orders.Add(order);
await db.SaveChangesAsync();        // نجح حفظ الطلب
await rabbitMq.PublishAsync(evt);   // السيرفر قد يقع قبل السطر!
```

إذا وقع السيرفر بين السطرين، بقي الطلب موجوداً لكن الـ Inventory لن تعرف عنه شيئاً. هذا **Dual-Write Problem**: نريد تثبيت معلومة في نظامين مستقلين (PostgreSQL وRabbitMQ)، ولا تستطيع Transaction عادية واحدة عبر EF Core ضمان نجاح العمليتين معاً.

ولو عكسنا ترتيب السطرين، قد تصل الرسالة بينما حفظ الطلب في قاعدة البيانات يفشل.

## 3) الحل: Transactional Outbox

نخزّن **الطلب ورسالة الحدث** في قاعدة بيانات المنتج (Ordering) ضمن **نفس الـ Transaction**، ولا نتصل بـ RabbitMQ داخل طلب HTTP:

```csharp
db.Orders.Add(order);

var messageId = db.EnqueueOutbox(
    "orders.placed.v1",
    new OrderPlacedV1(order.Id, order.Sku, order.Quantity));

await db.SaveChangesAsync();
```

هنا `EnqueueOutbox()` لا يرسل شيئاً! هو يضيف `OutboxMessage` إلى Change Tracker في **نفس DbContext**. عند `SaveChangesAsync`، يحصل Commit لكلا السجلين معاً أو Rollback لكليهما.

مثال على الجدولين بعد النجاح:

| Orders | |
|---|---|
| OrderId | ORDER-501 |
| Sku | BOOK-001 |
| Quantity | 3 |

| Outbox | |
|---|---|
| MessageId | EVENT-900 |
| EventType | orders.placed.v1 |
| PublishedAtUtc | null |

إذا وقع السيرفر بعد Commit، الرسالة لم تضع: هي محفوظة في Outbox.

## 4) مين يرسل الحدث؟ الـ Dispatcher

`OutboxBackgroundService` ينفّذ `OutboxDispatcher` بشكل دوري:

1. يبحث عن أحداث Pending التي حان وقت إعادة المحاولة.
2. يحجز السجل بعملية **Atomic Conditional UPDATE** ويحفظ `LeaseId` ووقت انتهاء الحجز.
3. يطلب من `IOutboxTransport` إرسال الرسالة. التطبيق النموذجي يوفر Adapter لـ RabbitMQ؛ المكتبة نفسها Broker-Agnostic.
4. ينتظر تأكيد RabbitMQ (Publisher Confirm)، ثم يحدّث `PublishedAtUtc`.
5. لو فشل النشر، يرفع `AttemptCount` ويحدد موعد Retry جديد مع Backoff.

الـ Lease يمنع Worker آخر من أخذ نفس السجل خلال مدة الحجز. لكنه **لا يضمن عدم تكرار النشر نهائياً**؛ إذا وقع Worker أو تأخر أكثر من مدة Lease، قد تنتشر نفس الرسالة مجدداً.

## 5) المشكلة الثانية: رسالة مكررة

السيناريو الدقيق:

1. الـ Dispatcher نشر `EVENT-900`.
2. RabbitMQ أكّد الاستلام.
3. الـ Inventory استقبل الحدث وحجز 3 كتب: **10 → 7**.
4. الـ Dispatcher وقع **قبل** تحديث `PublishedAtUtc`.
5. بعد عودة النظام وانتهاء الـ Lease، أعاد نشر نفس الحدث وبنفس `MessageId`.
6. إذا الـ Inventory كرّرت الحجز بلا حماية يصبح المخزون **7 → 4** بالغلط.

هون احتجنا **Idempotent Inbox**.

## 6) كيف الـ Inbox يمنع التكرار؟

في قاعدة بيانات Inventory جدول `reliable_inbox` فيه مفتاح مركب فريد:

`(ConsumerName, MessageId)`

الكود النموذجي لدى المستهلك يستخدم:

```csharp
var result = await inbox.ProcessAsync(
    db,
    consumerName: "inventory.order-reservation.v1",
    messageId: messageId,
    handle: async (context, ct) =>
    {
        // نفّذ التغييرات الحقيقية باستخدام نفس الـ DbContext
        await ReserveStockAsync(context, ct);
    },
    cancellationToken);

// بعد نجاح Transaction أو اكتشاف Duplicate مؤكّد:
// ACK لرسالة RabbitMQ
```

داخل `ProcessAsync` يصير الآتي ضمن **نفس Transaction**:

- يحاول إدراج Inbox Marker فريد.
- إذا الرسالة جديدة، ينفذ الـ Handler وتعديل المخزون، ثم يعمل Commit للاثنين.
- إذا نفس الرسالة مسجلة مسبقاً، يرجع `Duplicate` **من دون تنفيذ الـ Handler**.
- إذا فشل الـ Handler، يعمل Rollback أيضاً للـ Inbox Marker.

القيد الفريد مهم: مجرد استعلام `AnyAsync` لا يكفي عند وصول رسالتين بنفس الوقت؛ قد لا ترى أي واحدة منهما السجل قبل إدراجه، بينما قاعدة البيانات تفرض التفرد.

بالتالي إذا وصل `EVENT-900` مرتين، ينخفض المخزون مرة واحدة: **10 → 7 → 7**.

**تذكير:** من الضروري أيضاً وجود Business-Key Uniqueness عندما يصل حدثان **بمعرّفين مختلفين** لنفس Order؛ مثال الـ Inventory يملك `OrderId` مفتاحاً فريداً للحجوزات.

## 7) الفرق بين الـ ACK والـ Inbox

- `ACK` يعني إخبار RabbitMQ أن المستهلك أنهى التعامل مع الرسالة، فيستطيع Broker إزالة التسليم من قائمة الرسائل غير المؤكدة.
- لا ترسل ACK قبل Commit؛ لو وقع المستهلك بعد ACK وقبل حفظ المخزون فقد تضيع المعالجة.
- إذا حصل Commit ووقع المستهلك قبل ACK، RabbitMQ قد يعيد إرسالها؛ الـ Inbox سيعرف أنها Duplicate ثم يمكن ACK بأمان.

## 8) هل يعني Exactly-Once؟

**لا، مو على مستوى الشبكة.** التصميم يقدّم:

- كتابة ذرّية محلية للـ Order وOutbox ضمن قاعدة واحدة.
- قابلية إعادة المحاولة في النشر، وبالتالي يُتوقّع تكرار الـ Message.
- حماية من تكرار **آثار قاعدة البيانات نفسها** عند المستهلك طالما الـ Marker وتغييرات البيانات ضمن نفس Transaction.

أما Email، أو Payment Gateway، أو API خارجي، فآثارها خارج Transaction قاعدة البيانات وتتطلب Idempotency Keys أو تصميم تعويضات/ساغا منفصلاً. ولا توجد ضمانات ترتيب عام للأحداث.

## 9) لماذا مكتبة Generic؟

ليس فيها مفهوم `Order` أو `Product` أو `RabbitMQ` ضمن `src/ReliableFlow.EntityFrameworkCore`. هي توفر:

- `modelBuilder.AddReliableFlow()` لإضافة الجداول والقيود.
- `db.EnqueueOutbox(eventType, payload)` لإضافة أحداث لأي Domain.
- `AddReliableOutbox<YourDbContext>()` لتشغيل Dispatcher.
- `IOutboxTransport` كي تكتب Adapter لنوع Broker الذي تستخدمه.
- `AddReliableInbox<YourDbContext>()` و`InboxProcessor.ProcessAsync()` لعمليات المستهلك.

مثلاً يمكنك استخدامها مع:

- فاتورة: `billing.invoice-created.v1`.
- شحنة: `shipping.status-changed.v1`.
- طلب متجر: `orders.placed.v1`.

لكن **تثبيت NuGet وحده لا يضيف الحماية تلقائياً**: لازم تستعملها عند حدود العمليات التي تنشر أو تستهلك أحداثاً، وتحدد الـ Transaction وحدود ملكية البيانات.

## 10) ماذا جربنا فعلياً؟

في `docs/VERIFICATION.md` بتلاقي النتائج والتواريخ والأوامر، ومنها:

- اختبارات xUnit للـ Transactions والـ Rollback والتكرار.
- اختبار PostgreSQL لتنافس 32 Consumer على نفس MessageId، وتنافس 16 Dispatcher.
- اختبار حقيقي لـ RabbitMQ: إرسال طلب، فشل الناشر ثم التعافي، والنشر المكرر بعد Publisher Confirm.
- تجربة قتل Process حقيقية بعد Publisher Confirm وإعادة تشغيلها. **المخزون 100 → 99 والحجز واحد**، رغم حصول Duplicate.

لاحظ الفرق بين المثال التعليمي (بدأنا بـ 10 كتب) والـ Demo البرمجي الحقيقي (Seed يبدأ بـ 100 وحدة).

## 11) وين أبدأ بالمشروع؟

افتح الملفات بهذا الترتيب في Rider:

1. `samples/Ordering.Api/Program.cs` — Endpoint إنشاء الطلب.
2. `src/ReliableFlow.EntityFrameworkCore/OutboxWriter.cs` — حفظ الحدث محلياً.
3. `src/ReliableFlow.EntityFrameworkCore/OutboxDispatcher.cs` — إرسال الرسائل والـ Leases والـ Retries.
4. `samples/Ordering.Api/RabbitMqOutboxTransport.cs` — Adapter خاص بـ RabbitMQ.
5. `samples/Inventory.Service/OrderPlacedConsumer.cs` — استقبال الرسالة وACK بعد الحفظ.
6. `src/ReliableFlow.EntityFrameworkCore/InboxProcessor.cs` — منع التكرار ضمن Transaction.

ثم شغّل الاختبارات:

```powershell
dotnet build ReliableFlow.slnx -c Release
dotnet test ReliableFlow.slnx -c Release
./scripts/Run-LocalPostgresStress.ps1
./scripts/Run-PackageSmoke.ps1
```

وللمختبر المحلي المعزول في Windows + WSL، اقرأ `docs/OPERATIONS.md` قبل تشغيل `scripts/Run-LocalBrokerLab.ps1`؛ فهو ينشئ قواعد اختبار مؤقتة ويوقف خدمة Ordering بالقوة عمداً أثناء تجربة الـ Hard Kill.

**خلاصة تحفظها:** Outbox يضمن بقاء *نيّة نشر* الحدث مع بيانات العمل، وInbox يضمن عدم تكرار أثره داخل قاعدة بيانات المستهلك، رغم إمكانية تكرار تسليمه عبر Broker.
