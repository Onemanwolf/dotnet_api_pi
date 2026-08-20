using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// Pins the domain ↔ persistence agreement for the maximal tag blob
/// (audit finding F-04): <c>Resource.MaxTagCount</c> (50) tags of
/// <c>ResourceTag.MaxLength</c> (64) characters each must round-trip through
/// the real <c>ApiDbContext</c> value conversion without exceeding the
/// 4,096-character cap declared on the Tags column.
/// <para>
/// Note: SQLite does not enforce declared column lengths, so this test is the
/// thing that catches the disagreement. On a store that does enforce them
/// (e.g. SQL Server, PostgreSQL), a blob longer than the cap would surface as
/// a truncation error — hence the explicit length assertion below.
/// </para>
/// </summary>
public sealed class TagBlobBoundaryTests
{
    /// <summary>
    /// The cap <c>ApiDbContext</c> declares on the Tags column.
    /// </summary>
    private const int TagsColumnCap = 4096;

    [Fact]
    public async Task MaximalTagSet_RoundTrips_WithinTagsColumnCap()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        // Exactly MaxTagCount tags of exactly MaxLength characters each
        // (60 'a's + a 4-digit suffix keeps every tag distinct, lower-case
        // and whitespace-free, so normalization is a no-op).
        var resource = Resource.Create(
            new ResourceName("Maximal Tags"),
            tags: Enumerable.Range(0, Resource.MaxTagCount)
                .Select(static i => new ResourceTag(
                    new string('a', ResourceTag.MaxLength - 4) + i.ToString("0000"))));

        Assert.Equal(Resource.MaxTagCount, resource.Tags.Length);
        Assert.All(
            resource.Tags,
            static tag => Assert.Equal(ResourceTag.MaxLength, tag.Value.Length));

        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();

        // (a) The blob persisted by the value conversion must fit the column
        // cap. Read straight from the database, i.e. exactly what a real
        // relational store would store.
        var blobLength = ReadTagsColumnLength(db, resource.Id);
        Assert.True(
            blobLength <= TagsColumnCap,
            $"Persisted tag blob is {blobLength} characters; column cap is {TagsColumnCap}.");
        Assert.True(
            blobLength > Resource.MaxTagCount * ResourceTag.MaxLength,
            $"Blob ({blobLength}) should exceed the raw 3,200 characters because of JSON punctuation.");

        // (b) Every tag round-trips with exact value equality and order.
        await using var fresh = db.NewContext();
        var loaded = (await fresh.Resources.FindAsync(resource.Id))!;

        Assert.Equal(
            resource.Tags.Select(static tag => tag.Value).ToArray(),
            loaded.Tags.Select(static tag => tag.Value).ToArray());
    }

    /// <summary>
    /// Reads the stored length of the Tags column for one resource.
    /// </summary>
    private static int ReadTagsColumnLength(InMemoryDatabase db, Guid resourceId)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT LENGTH(Tags) FROM Resources WHERE Id = @id;";
        command.Parameters.Add(new SqliteParameter("@id", resourceId));
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
