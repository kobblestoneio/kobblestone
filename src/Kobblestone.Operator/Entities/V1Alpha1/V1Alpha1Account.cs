using k8s.Models;

using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

namespace Kobblestone.Operator.Entities.V1Alpha1;

[Description("""
             `Account` represents a Microsoft that can be used to authenticate against online Minecraft servers.
             They can be referenced by `Bot`s to connect to online servers.

             Once an `Account` is created, an authorization flow (OAuth device flow) is initiated.
             The field `status.deviceFlow` will contain all necessary information for users to complete the authorization.

             Once authorization is completed, the `Account` will store access and refresh tokens securely within a managed `Secret`. 

             `Account`s automatically refresh their session before it expires for as long as the authorization state is valid. 
             """)]
[KubernetesEntity(Group = "kobblestone.io", ApiVersion = "v1alpha1", Kind = "Account")]
public sealed class
    V1Alpha1Account :
    CustomKubernetesEntity<V1Alpha1Account.V1Alpha1AccountSpec, V1Alpha1Account.V1Alpha1AccountStatus>,
    IConditionsStatus<V1Alpha1Account.V1Alpha1AccountStatus, V1Condition>
{
    public record V1Alpha1AccountSpec
    {
        [Description("""
                     Whether this account is considered inactive and will not attempt any authorization.

                     Inactive accounts will permanently stay in phase `Unauthorized`.

                     Default is `false`.
                     """)]
        public bool? Inactive { get; set; }

        [Description("""
                     Configures the max. leftover TTL of sessions in minutes before they are refreshed.

                     Default is `60` (1 hour).
                     """)]
        [RangeMinimum(5)]
        public int? RefreshBeforeExpiryMinutes { get; set; }

        [Description("""
                     Configurations related to the authorization flows.
                     """)]
        public V1Alpha1AccountAuthorization? Authorization { get; set; }
    }

    public record V1Alpha1AccountStatus : IConditions<V1Condition>
    {
        [Description("""
                     Current phase of the `Account`.

                     Values:
                     - `Unauthorized`: The `Account` is not authorized and is currently not awaiting authorization.
                     - `AwaitingAuthorization`: The `Account` awaits manual authorization via the device flow.
                     - `Authorized`: The `Account` was successfully authorized and is considered ready.
                     """)]
        [AdditionalPrinterColumn(name: "Status")]
        public AccountPhase Phase { get; set; }

        [Description("""
                     Message explaining the current state of the `Account`.
                     """)]
        [AdditionalPrinterColumn(name: "Message")]
        public string? Message { get; set; }

        [Description("""
                     Timestamp when this `Account` was last authorized and a session was created.
                     """)]
        public DateTime? AuthorizedAt { get; set; }

        [Description("""
                     Timestamp when the current session expires.
                     """)]
        public DateTime? SessionExpiresAt { get; set; }

        [Description("""
                     Contains information for completing the device flow authorization.

                     Only set when `status.phase` is `AwaitingAuthorization`.
                     """)]
        public V1Alpha1AccountStatusDeviceFlow? DeviceFlow { get; set; }

        [Description("""
                     Contains user profile information.

                     Only set when `status.phase` is `Authorized`.
                     """)]
        public V1Alpha1AccountProfile? Profile { get; set; }

        [Description("""
                     Conditions of the `Account`.
                     """)]
        public IList<V1Condition> Conditions { get; set; } = [];
    }

    public enum AccountPhase
    {
        Unauthorized,
        AwaitingAuthorization,
        Authorized
    }

    public record V1Alpha1AccountAuthorization
    {
        [Description("""
                     Configures the managed `Pod` resource that is spawned for authorization flows.
                     """)]
        public V1Alpha1AccountAuthorizationPod? Pod { get; set; }
    }

    public record V1Alpha1AccountAuthorizationPod
    {
        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? Image { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Container")]
        public string? ImagePullPolicy { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#Affinity")]
        public V1Affinity? Affinity { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public string? NodeName { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IDictionary<string, string>? NodeSelector { get; set; }

        [ExternalDocs("https://kubernetes.io/docs/reference/kubernetes-api/core/pod-v1/#PodSpec")]
        public IList<V1Toleration>? Tolerations { get; set; }
    }


    public record V1Alpha1AccountStatusDeviceFlow
    {
        [Description("""
                     Code with which the flow may be authorized.
                     """)]
        [Required]
        public string UserCode { get; set; } = null!;

        [Description("""
                     Timestamp when the code expires.
                     """)]
        public DateTime? ExpiresAt { get; set; }

        [Description("""
                     URI where verification may take place.
                     """)]
        [Required]
        [Format("uri")]
        public string VerificationUri { get; set; } = null!;

        [Description("""
                     Message that can be shown to the human from whom action is required.
                     """)]
        public string? Message { get; set; }
    }

    public record V1Alpha1AccountProfile
    {
        [Description("""
                     Name of the user.
                     """)]
        [AdditionalPrinterColumn(name: "Username")]
        public string? Name { get; set; }

        [Description("""
                     UUID of the user.
                     """)]
        [AdditionalPrinterColumn(name: "UUID")]
        public string? Uuid { get; set; }
    }
}