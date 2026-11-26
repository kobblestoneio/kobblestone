using KubeOps.KubernetesClient.Selectors;

namespace Kobblestone.Operator.Utils;

/// <summary>
/// Spec fields of Kobblestone resources that the API server can filter by (CRD <c>selectableFields</c>). They let a
/// controller list the resources that reference an object, e.g. the `Backup`s of a `Server`, with a field selector
/// instead of listing every resource of the kind and filtering by spec.
/// </summary>
/// <remarks>
/// KubeOps does not generate <c>selectableFields</c>, so the CRDs declare them in
/// <c>SelectableFields.targets</c>, which adds them to the generated CRDs. Every field used here must be declared
/// there, otherwise the API server rejects the list request. A selectable field cannot be inside a list (e.g.
/// <c>spec.targets</c> of a `FileBrowser`).
/// </remarks>
public static class SelectableFields
{
    /// <summary>
    /// Name of the target of a `Backup` or `Restore` (<c>spec.targetRef.name</c>). The target's kind still has to be
    /// checked.
    /// </summary>
    public const string TargetName = "spec.targetRef.name";

    /// <summary>
    /// Name of the `Router` a `Route` attaches to (<c>spec.parentRef.name</c>).
    /// </summary>
    public const string ParentRouterName = "spec.parentRef.name";

    /// <summary>
    /// Name of the `Account` a `Bot` authenticates with (<c>spec.auth.accountRef.name</c>).
    /// </summary>
    public const string AccountName = "spec.auth.accountRef.name";

    /// <summary>
    /// Name of the volume of a `BackupRepo` (<c>spec.persistentVolumeClaim.name</c>), which several repos can share
    /// with different <c>subPath</c>s.
    /// </summary>
    public const string RepoClaimName = "spec.persistentVolumeClaim.name";

    /// <summary>
    /// Field selector expression matching resources whose <paramref name="field"/> equals <paramref name="value"/>.
    /// </summary>
    public static string EqualTo(string field, string value) =>
        new FieldSelector[] { new EqualsFieldSelector(field, value) }.ToExpression();
}
