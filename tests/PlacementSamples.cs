namespace Haby.Tests;

internal static class PlacementSamples
{
    internal const string Minimal = """{"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard-runtime","revision":1},"spec":{"namespace":"apps"}}""";
    internal const string Full = """
        {"apiVersion":"haby.dev/v1alpha1","kind":"KubernetesPlacement","metadata":{"name":"standard-runtime","revision":1},"spec":{
          "namespace":"apps","registry":"registry.example.test:5000","repositoryPrefix":"product/team",
          "nodeSelector":{"example.test/pool":"apps","disk":"ssd"},"imagePullSecrets":["registry-credentials","second"],
          "replicas":3,"strategy":{"type":"RollingUpdate","maxSurge":"25%","maxUnavailable":0},
          "minReadySeconds":10,"rolloutSeconds":900,"resources":{"requests":{"cpu":"250m","memory":"128Mi"},"limits":{"cpu":"1","memory":"1Gi"}},
          "service":{"enabled":true},"headlessService":{"enabled":true},"ingress":{"enabled":true,"className":"nginx","domain":"example.test","tlsSecret":"existing-tls"}
        }}
        """;
}
