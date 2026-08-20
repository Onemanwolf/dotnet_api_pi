using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DotNetApiPi.Infrastructure.Persistence.Mongo;

/// <summary>
/// The MongoDB document representation of the <see cref="Domain.Entities.Resource"/>
/// aggregate. Value objects are flattened to their primitive values (name, tags)
/// and the status enum is stored as its string name, mirroring how the EF Core
/// model persists the same aggregate. Keeping the document separate from the
/// aggregate means the domain model never depends on the MongoDB driver.
/// </summary>
public sealed class ResourceDocument
{
    /// <summary>
    /// Gets or sets the identity of the resource. Mapped to the Mongo <c>_id</c> field.
    /// <para>
    /// Driver 3.x defaults Guid serialization to <c>GuidRepresentation.Unspecified</c>,
    /// which throws when serializing, so the representation must be set explicitly.
    /// The <c>[BsonGuidRepresentation]</c> attribute is the documented 3.x way to
    /// configure this for automapped POCOs; the <c>Standard</c> format (BSON
    /// binary subtype 4) is the recommended one for new deployments.
    /// </para>
    /// </summary>
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the resource name (the <c>Value</c> of the name value object).
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Gets or sets the optional description of the resource.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the status of the resource, stored as the enum's name.
    /// </summary>
    public string Status { get; set; } = default!;

    /// <summary>
    /// Gets or sets the resource tags (the <c>Value</c> of each tag value object).
    /// </summary>
    public List<string> Tags { get; set; } = [];
}
