using Gecko.Notification.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gecko.Notification.Infrastructure.Persistence.Converters;

/// <summary>
/// Bridges domain value objects to the scalar columns they live in.
///
/// A ValueConverter is a pair of expression trees — one to the database, one
/// back. They must be EXPRESSIONS, not lambdas calling arbitrary code, because
/// EF translates them into the query tree. That is why each converter calls a
/// single static factory rather than doing work inline.
///
/// THE DIRECTION THAT BITES PEOPLE: the "from database" side must NOT
/// re-validate as if the value were new user input. Rows already in the table
/// were valid when written; a stricter rule added later would make old rows
/// unreadable and take the whole table down. Note especially that
/// <see cref="IdempotencyKey"/> converts back via FromExisting, never Compute —
/// recomputing on read would be catastrophic, because the read path does not
/// have the five source components and would silently produce a different key.
/// </summary>
public static class ValueObjectConverters
{
    public static readonly ValueConverter<ChannelCode, string> ChannelCode =
        new(vo => vo.Value,
            db => Domain.ValueObjects.ChannelCode.From(db));

    public static readonly ValueConverter<EventTypeCode, string> EventTypeCode =
        new(vo => vo.Value,
            db => Domain.ValueObjects.EventTypeCode.From(db));

    public static readonly ValueConverter<RecipientAddress, string> RecipientAddress =
        new(vo => vo.Value,
            db => Domain.ValueObjects.RecipientAddress.From(db));

    public static readonly ValueConverter<Locale, string> Locale =
        new(vo => vo.Value,
            db => Domain.ValueObjects.Locale.From(db));

    public static readonly ValueConverter<IdempotencyKey, string> IdempotencyKey =
        new(vo => vo.Value,
            db => Domain.ValueObjects.IdempotencyKey.FromExisting(db));
}
