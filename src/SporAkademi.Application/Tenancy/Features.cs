using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy;

/// <summary>A flag as the Platform Admin sees it for one school (P4-10).</summary>
/// <param name="Enabled">The effective value for the school.</param>
/// <param name="DefaultEnabled">The global default (configuration, then the catalog), used while the school has no value of its own.</param>
/// <param name="Overridden">Whether the school has its own value.</param>
public sealed record SchoolFeatureDto(string Key, string Description, bool Enabled, bool DefaultEnabled, bool Overridden);

/// <summary>The caller's school's enabled flags (P4-10); disabled and unknown flags are simply absent.</summary>
public sealed record EnabledFeaturesDto(IReadOnlyList<string> Enabled);

public sealed record ListSchoolFeaturesQuery(Guid SchoolId) : IQuery<IReadOnlyList<SchoolFeatureDto>>;

public sealed record GetEnabledFeaturesQuery : IQuery<EnabledFeaturesDto>;

public sealed record SetSchoolFeatureCommand(Guid SchoolId, string Key, bool Enabled) : ICommand<Unit>;

/// <summary>The schools' own values (P4-10). The Platform Admin writes them; a school user's scope only sees its own school's rows.</summary>
public interface ISchoolFeatureRepository
{
    /// <summary>The school's own values by key; keys without a row use the global default.</summary>
    Task<Dictionary<string, bool>> FindValuesAsync(SchoolId school, CancellationToken cancellationToken);

    Task<SchoolFeature?> GetAsync(SchoolId school, string key, CancellationToken cancellationToken);

    void Add(SchoolFeature feature);
}

/// <summary>The global defaults (DEC-582): <c>Features:Defaults:{key}</c> in the configuration, else the catalog's default.</summary>
public interface IFeatureDefaults
{
    bool IsEnabledByDefault(FeatureDefinition feature);
}

/// <summary>
/// Whether a feature is on for the caller's school (P4-10). Used by the endpoint gate (a disabled feature's endpoints
/// answer 404) and by <c>GET /api/v1/features</c>. The school's values are cached per school (DEC-583); the Platform
/// Admin's change invalidates them.
/// </summary>
public interface IFeatureFlags
{
    /// <summary>False for an unknown key, and for a caller without a school.</summary>
    Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken);

    /// <summary>The keys enabled for the caller's school, in catalog order.</summary>
    Task<IReadOnlyList<string>> EnabledAsync(CancellationToken cancellationToken);
}

internal sealed class FeatureFlags(
    ICurrentUser currentUser,
    ITenantCache cache,
    ISchoolFeatureRepository features,
    IFeatureDefaults defaults) : IFeatureFlags
{
    public async Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken) =>
        FeatureCatalog.Find(key) is { } feature && (await EnabledAsync(cancellationToken)).Contains(feature.Key);

    public async Task<IReadOnlyList<string>> EnabledAsync(CancellationToken cancellationToken)
    {
        if (currentUser.TenantId is not { } tenant)
        {
            return [];
        }

        var school = SchoolId.From(tenant);
        var values = await cache.GetOrCreateAsync(
            TenantCacheKey.For(school, CacheArea.Features, "values"),
            async ct => await features.FindValuesAsync(school, ct),
            cancellationToken);
        return [.. FeatureCatalog.All
            .Where(f => values.TryGetValue(f.Key, out var own) ? own : defaults.IsEnabledByDefault(f))
            .Select(f => f.Key)];
    }
}

internal sealed class GetEnabledFeaturesHandler(IFeatureFlags flags) : IQueryHandler<GetEnabledFeaturesQuery, EnabledFeaturesDto>
{
    public async Task<Result<EnabledFeaturesDto>> Handle(GetEnabledFeaturesQuery query, CancellationToken cancellationToken) =>
        Result.Success(new EnabledFeaturesDto(await flags.EnabledAsync(cancellationToken)));
}

/// <summary>Platform Admin only. Not cached: read on the school's page, and it must show the stored value at once.</summary>
internal sealed class ListSchoolFeaturesHandler(ISchoolRepository schools, ISchoolFeatureRepository features, IFeatureDefaults defaults)
    : IQueryHandler<ListSchoolFeaturesQuery, IReadOnlyList<SchoolFeatureDto>>
{
    public async Task<Result<IReadOnlyList<SchoolFeatureDto>>> Handle(ListSchoolFeaturesQuery query, CancellationToken cancellationToken)
    {
        var school = SchoolId.From(query.SchoolId);
        if (await schools.FindDtoAsync(school, cancellationToken) is null)
        {
            return Result.Failure<IReadOnlyList<SchoolFeatureDto>>(SchoolErrors.NotFound);
        }

        var values = await features.FindValuesAsync(school, cancellationToken);
        IReadOnlyList<SchoolFeatureDto> list =
        [
            .. FeatureCatalog.All.Select(f =>
            {
                var byDefault = defaults.IsEnabledByDefault(f);
                var overridden = values.TryGetValue(f.Key, out var own);
                return new SchoolFeatureDto(f.Key, f.Description, overridden ? own : byDefault, byDefault, overridden);
            }),
        ];
        return Result.Success(list);
    }
}

/// <summary>Platform Admin only. The school's cached values are dropped after the commit (DEC-583).</summary>
internal sealed class SetSchoolFeatureHandler(ISchoolRepository schools, ISchoolFeatureRepository features)
    : ICommandHandler<SetSchoolFeatureCommand, Unit>
{
    public async Task<Result<Unit>> Handle(SetSchoolFeatureCommand command, CancellationToken cancellationToken)
    {
        var school = SchoolId.From(command.SchoolId);
        if (await schools.FindDtoAsync(school, cancellationToken) is null)
        {
            return Result.Failure<Unit>(SchoolErrors.NotFound);
        }

        if (FeatureCatalog.Find(command.Key) is null)
        {
            return Result.Failure<Unit>(FeatureErrors.NotFound);
        }

        if (await features.GetAsync(school, command.Key, cancellationToken) is { } existing)
        {
            existing.Set(command.Enabled);
            return Result.Success(Unit.Value);
        }

        var created = SchoolFeature.Create(school, command.Key, command.Enabled);
        if (created.IsFailure)
        {
            return Result.Failure<Unit>(created.Error);
        }

        features.Add(created.Value);
        return Result.Success(Unit.Value);
    }
}
