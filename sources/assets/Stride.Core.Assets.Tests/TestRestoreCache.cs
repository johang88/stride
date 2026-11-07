// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using NuGet.Frameworks;
using NuGet.ProjectModel;
using NuGet.Versioning;
using Stride.Core.Packages;

namespace Stride.Core.Assets.Tests;

/// <summary>
/// The cached companion package restore of <see cref="NugetStore.RestorePackage"/>: reused only for the same request while
/// every package it lists is installed with the content it had.
/// </summary>
public class TestRestoreCache : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "stride-restore-cache-test-" + Guid.NewGuid().ToString("N"));
    private string PackagesFolder => Path.Combine(root, "packages");
    private string OutputPath => Path.Combine(root, "output");
    private string MetadataFile => Path.Combine(PackagesFolder, "my.package", "1.0.0-dev", ".nupkg.metadata");

    public TestRestoreCache()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MetadataFile)!);
        WriteInstalledHash("HASH1");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private void WriteInstalledHash(string hash)
        => File.WriteAllText(MetadataFile, $$"""{ "version": 2, "contentHash": "{{hash}}", "source": "local" }""");

    private LockFile CreateLockFile()
    {
        var lockFile = new LockFile { Version = 3 };
        lockFile.PackageFolders.Add(new LockFileItem(PackagesFolder + Path.DirectorySeparatorChar));
        lockFile.Libraries.Add(new LockFileLibrary { Name = "My.Package", Version = NuGetVersion.Parse("1.0.0-dev"), Type = "package", Path = "my.package/1.0.0-dev", Sha512 = "HASH1" });
        lockFile.Targets.Add(new LockFileTarget { TargetFramework = NuGetFramework.Parse("net10.0") });
        return lockFile;
    }

    [Fact]
    public void TestSameRequestReusesTheLockFile()
    {
        NugetStore.WriteRestoreCache(OutputPath, "request", CreateLockFile());

        var cached = NugetStore.TryReadRestoreCache(OutputPath, "request");
        Assert.NotNull(cached);
        Assert.Equal("My.Package", Assert.Single(cached.Libraries).Name);
    }

    [Fact]
    public void TestAnotherRequestRestoresAgain()
    {
        NugetStore.WriteRestoreCache(OutputPath, "request", CreateLockFile());

        Assert.Null(NugetStore.TryReadRestoreCache(OutputPath, "another request"));
    }

    [Fact]
    public void TestRepackedPackageRestoresAgain()
    {
        NugetStore.WriteRestoreCache(OutputPath, "request", CreateLockFile());

        // A local build packed again under the same version
        WriteInstalledHash("HASH2");
        Assert.Null(NugetStore.TryReadRestoreCache(OutputPath, "request"));
    }

    [Fact]
    public void TestRemovedPackageRestoresAgain()
    {
        NugetStore.WriteRestoreCache(OutputPath, "request", CreateLockFile());

        Directory.Delete(Path.GetDirectoryName(MetadataFile)!, true);
        Assert.Null(NugetStore.TryReadRestoreCache(OutputPath, "request"));
    }

    [Fact]
    public void TestNoCacheRestores()
    {
        Assert.Null(NugetStore.TryReadRestoreCache(OutputPath, "request"));
    }
}
