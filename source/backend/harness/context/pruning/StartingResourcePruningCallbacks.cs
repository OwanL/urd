using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Sessions;

namespace Urd.Core.Context;

// Chooses the model that selects resources for one pruning category.
internal delegate Task<AuxiliaryModelResolution> ResolvePruningModel(
    AuxiliaryModelPurpose purpose, CancellationToken cancellation);

// Saves a category's pruning status with the candidates it considered and, once settled, the identities it retained.
internal delegate Task SavePruningProgress(
    StartingResourcePruningStatus status, IReadOnlyList<PrepassResource> candidates, IReadOnlyList<string>? retainedIds);
