using Xunit;

// Docker-backed SSH and daemon fixtures share host CPU, network ports, and the Docker daemon.
// Serializing this test assembly keeps the Interop gate deterministic while hermetic tests remain
// fast and parallel in their own assemblies.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
