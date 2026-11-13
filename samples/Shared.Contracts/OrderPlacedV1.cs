namespace Shared.Contracts;

public static class EventNames
{
    public const string OrderPlacedV1 = "orders.placed.v1";
    public const string OrderPlacedDeadLetter = "orders.placed.v1.dlq";
}

public sealed record OrderPlacedV1(Guid OrderId, string Sku, int Quantity);
