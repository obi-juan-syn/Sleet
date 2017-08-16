using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging.Core;
using SleetLib;

namespace Sleet
{
    public class Symbols : ISleetService, ISymbolsAddRemovePackages, ISymbolsPackagesLookup, IValidatableService, IPackagesLookup
    {
        private readonly SleetContext _context;

        public string Name => nameof(Symbols);

        public PackageIndexFile PackageIndex { get; }

        public Symbols(SleetContext context)
        {
            _context = context;
            PackageIndex = new PackageIndexFile(context, SymbolsIndexUtility.PackageIndexPath);
        }

        public Task AddPackageAsync(PackageInput packageInput)
        {
            return AddPackageAsync(packageInput, isSymbolsPackage: false);
        }

        private async Task AddPackageAsync(PackageInput packageInput, bool isSymbolsPackage)
        {
            // Read dll/pdb files from the package.
            var assemblies = await GetAssembliesAsync(packageInput);

            if (assemblies.Count > 0)
            {
                var tasks = new List<Task>();

                // Add the id/version to the package index.
                if (isSymbolsPackage)
                {
                    tasks.Add(PackageIndex.AddPackageAsync(packageInput));
                }

                // Add dll/pdb files to the feed.
                tasks.AddRange(assemblies.Select(e => AddAssemblyAsync(e, packageInput)));

                // Add assembly -> package reverse lookup
                tasks.AddRange(assemblies.Select(e => AddAssemblyToPackageIndexAsync(packageInput, e.IndexFile, isSymbolsPackage: isSymbolsPackage)));

                // Add index of all dll/pdb files added for the package.
                tasks.AddRange(assemblies.Select(e => AddPackageToAssemblyIndexAsync(packageInput.Identity, assemblies, isSymbolsPackage: isSymbolsPackage)));

                // Wait for everything to finish
                await Task.WhenAll(tasks);
            }
            else
            {
                await _context.Log.LogAsync(LogLevel.Verbose, $"No files found that could be added to the symbols feed. Skipping package {packageInput.Identity}");
            }
        }

        public Task RemovePackageAsync(PackageIdentity package)
        {
            return Task.FromResult<bool>(false);
        }

        public Task AddSymbolsPackageAsync(PackageInput packageInput)
        {
            return AddPackageAsync(packageInput, isSymbolsPackage: true);
        }

        public Task RemoveSymbolsPackageAsync(PackageIdentity package)
        {
            throw new NotImplementedException();
        }

        public Task<ISet<PackageIdentity>> GetPackagesAsync()
        {
            return PackageIndex.GetPackagesAsync();
        }

        public Task<ISet<PackageIdentity>> GetPackagesByIdAsync(string packageId)
        {
            return PackageIndex.GetPackagesByIdAsync(packageId);
        }

        public Task<ISet<PackageIdentity>> GetSymbolsPackagesAsync()
        {
            return PackageIndex.GetSymbolsPackagesAsync();
        }

        public Task<ISet<PackageIdentity>> GetSymbolsPackagesByIdAsync(string packageId)
        {
            return PackageIndex.GetSymbolsPackagesByIdAsync(packageId);
        }

        private async Task AddAssemblyAsync(PackageFile assembly, PackageInput packageInput)
        {
            var file = _context.Source.Get(SymbolsIndexUtility.GetAssemblyFilePath(assembly.FileName, assembly.Hash));

            if (await file.Exists(_context.Log, _context.Token) == false)
            {
                // Write assembly
                var stream = await packageInput.RunWithLockAsync(
                    (p) => assembly.ZipEntry.Open().AsMemoryStreamAsync());

                using (stream)
                {
                    await file.Write(stream, _context.Log, _context.Token);
                }
            }
        }

        private ISleetFile GetFile(string fileName, string hash)
        {
            var symbolsPath = SymbolsUtility.GetSymbolsServerPath(fileName, hash);

            return _context.Source.Get($"/symbols/{symbolsPath}");
        }

        /// <summary>
        /// file/hash/index -> package
        /// </summary>
        private Task AddAssemblyToPackageIndexAsync(PackageInput package, ISleetFile indexFile, bool isSymbolsPackage)
        {
            var index = new PackageIndexFile(_context, indexFile, persistWhenEmpty: false);

            if (isSymbolsPackage)
            {
                return index.AddSymbolsPackageAsync(package);
            }
            else
            {
                return index.AddPackageAsync(package);
            }
        }

        private Task AddPackageToAssemblyIndexAsync(PackageIdentity package, List<PackageFile> assemblies, bool isSymbolsPackage)
        {
            var path = SymbolsIndexUtility.GetPackageToAssemblyIndexPath(package);
            var index = new AssetIndexFile(_context, path, package);

            var assets = assemblies.Select(e => new AssetIndexEntry(e.AssetFile.EntityUri, e.IndexFile.EntityUri));

            if (isSymbolsPackage)
            {
                return index.AddSymbolsAssetsAsync(assets);
            }
            else
            {
                return index.AddAssetsAsync(assets);
            }
        }

        private async Task<List<PackageFile>> GetAssembliesAsync(PackageInput packageInput)
        {
            var result = new List<PackageFile>();
            var seen = new HashSet<ISleetFile>();

            var assemblyFiles = packageInput.Zip.Entries
                .Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pdbFiles = packageInput.Zip.Entries
                .Where(e => e.FullName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var assembly in assemblyFiles)
            {
                string assemblyHash = null;
                string pdbHash = null;
                ZipArchiveEntry pdbEntry = null;
                var valid = false;

                try
                {
                    using (var stream = await assembly.Open().AsMemoryStreamAsync())
                    using (var reader = new PEReader(stream))
                    {
                        assemblyHash = SymbolsUtility.GetSymbolHashFromAssembly(reader);
                        pdbHash = SymbolsUtility.GetPDBHashFromAssembly(reader);
                    }

                    var assemblyWithoutExt = SleetLib.PathUtility.GetFullPathWithoutExtension(assembly.FullName);

                    pdbEntry = pdbFiles.FirstOrDefault(e =>
                        StringComparer.OrdinalIgnoreCase.Equals(
                            SleetLib.PathUtility.GetFullPathWithoutExtension(e.FullName), assemblyWithoutExt));

                    valid = true;
                }
                catch
                {
                    // Ignore bad assemblies
                    var message = LogMessage.Create(LogLevel.Warning, $"Unable add symbols for {assembly.FullName}, this file will not be present in the symbol server.");
                    await _context.Log.LogAsync(message);
                }

                if (valid)
                {
                    // Add .dll
                    var fileInfo = new FileInfo(assembly.FullName);
                    var dllFile = _context.Source.Get(SymbolsIndexUtility.GetAssemblyFilePath(fileInfo.Name, assemblyHash));
                    var indexFile = _context.Source.Get(SymbolsIndexUtility.GetAssemblyToPackageIndexPath(fileInfo.Name, assemblyHash));

                    // Avoid duplicates
                    if (seen.Add(dllFile))
                    {
                        result.Add(new PackageFile(fileInfo.Name, assemblyHash, assembly, dllFile, indexFile));
                    }

                    // Add .pdb
                    if (pdbEntry != null)
                    {
                        var pdbFileInfo = new FileInfo(pdbEntry.FullName);
                        var pdbFile = _context.Source.Get(SymbolsIndexUtility.GetAssemblyFilePath(pdbFileInfo.Name, pdbHash));
                        var pdbIndexFile = _context.Source.Get(SymbolsIndexUtility.GetAssemblyToPackageIndexPath(pdbFileInfo.Name, pdbHash));

                        // Avoid duplicates
                        if (seen.Add(pdbFile))
                        {
                            result.Add(new PackageFile(pdbFileInfo.Name, pdbHash, pdbEntry, pdbFile, pdbIndexFile));
                        }
                    }
                }
            }

            return result;
        }

        public async Task<IReadOnlyList<ILogMessage>> ValidateAsync()
        {
            var messages = new List<ILogMessage>();

            var packages = await GetPackagesAsync();
            var symbolsPackages = await GetSymbolsPackagesAsync();

            // De-dupe index files between packages to avoid threading conflicts
            var assetIndexFiles = new Dictionary<PackageIdentity, AssetIndexFile>();

            foreach (var package in packages.Concat(symbolsPackages))
            {
                if (!assetIndexFiles.ContainsKey(package))
                {
                    assetIndexFiles.Add(package, GetAssetIndexFile(package));
                }
            }

            // Retrieve all indexes in parallel
            await Task.WhenAll(assetIndexFiles.Values.Select(e => e.File.FetchAsync(_context.Log, _context.Token)));

            // Find all reverse lookup indexes for assembly files
            var assemblyIndexFiles = new Dictionary<Uri, PackageIndexFile>();

            // 1. Build a mapping for every assembly of the parents (symbols and non-symbols).
            // 2. Verify that the assembly -> package index contains the same identities.
            // 3. Verify no additional packages are mapped.

            return messages;
        }

        private AssetIndexFile GetAssetIndexFile(PackageIdentity package)
        {
            var path = SymbolsIndexUtility.GetPackageToAssemblyIndexPath(package);
            return new AssetIndexFile(_context, path, package);
        }

        /// <summary>
        /// dll or pdb file
        /// </summary>
        private class PackageFile
        {
            public string FileName { get; }

            public string Hash { get; }

            public ZipArchiveEntry ZipEntry { get; }

            public ISleetFile AssetFile { get; }

            public ISleetFile IndexFile { get; }

            public PackageFile(string fileName, string hash, ZipArchiveEntry zipEntry, ISleetFile assetFile, ISleetFile indexFile)
            {
                AssetFile = assetFile;
                FileName = fileName;
                Hash = hash;
                ZipEntry = zipEntry;
                IndexFile = indexFile;
            }
        }
    }
}
