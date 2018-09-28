// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Packaging.Licenses;
using NuGet.Versioning;

namespace NuGet.Packaging
{
    /// <summary>
    /// Reads .nuspec files
    /// </summary>
    public class NuspecReader : NuspecCoreReaderBase
    {
        // node names
        private const string Dependencies = "dependencies";
        private const string Group = "group";
        private const string TargetFramework = "targetFramework";
        private const string Dependency = "dependency";
        private const string References = "references";
        private const string Reference = "reference";
        private const string File = "file";
        private const string FrameworkAssemblies = "frameworkAssemblies";
        private const string FrameworkAssembly = "frameworkAssembly";
        private const string AssemblyName = "assemblyName";
        private const string Language = "language";
        private const string ContentFiles = "contentFiles";
        private const string Files = "files";
        private const string BuildAction = "buildAction";
        private const string Flatten = "flatten";
        private const string CopyToOutput = "copyToOutput";
        private const string IncludeFlags = "include";
        private const string ExcludeFlags = "exclude";
        private const string LicenseUrl = "licenseUrl";
        private const string Repository = "repository";
        private const string License = "license";
        private static readonly char[] CommaArray = new char[] { ',' };
        private readonly IFrameworkNameProvider _frameworkProvider;

        /// <summary>
        /// Nuspec file reader.
        /// </summary>
        public NuspecReader(string path)
            : this(path, DefaultFrameworkNameProvider.Instance)
        {
        }

        /// <summary>
        /// Nuspec file reader.
        /// </summary>
        public NuspecReader(string path, IFrameworkNameProvider frameworkProvider)
            : base(path)
        {
            _frameworkProvider = frameworkProvider;
        }


        /// <summary>
        /// Nuspec file reader
        /// </summary>
        /// <param name="stream">Nuspec file stream.</param>
        public NuspecReader(Stream stream)
            : this(stream, DefaultFrameworkNameProvider.Instance, leaveStreamOpen: false)
        {

        }

        /// <summary>
        /// Nuspec file reader
        /// </summary>
        /// <param name="xml">Nuspec file xml data.</param>
        public NuspecReader(XDocument xml)
            : this(xml, DefaultFrameworkNameProvider.Instance)
        {

        }

        /// <summary>
        /// Nuspec file reader
        /// </summary>
        /// <param name="stream">Nuspec file stream.</param>
        /// <param name="frameworkProvider">Framework mapping provider for NuGetFramework parsing.</param>
        public NuspecReader(Stream stream, IFrameworkNameProvider frameworkProvider, bool leaveStreamOpen)
            : base(stream, leaveStreamOpen)
        {
            _frameworkProvider = frameworkProvider;
        }

        /// <summary>
        /// Nuspec file reader
        /// </summary>
        /// <param name="xml">Nuspec file xml data.</param>
        /// <param name="frameworkProvider">Framework mapping provider for NuGetFramework parsing.</param>
        public NuspecReader(XDocument xml, IFrameworkNameProvider frameworkProvider)
            : base(xml)
        {
            _frameworkProvider = frameworkProvider;
        }

        /// <summary>
        /// Read package dependencies for all frameworks
        /// </summary>
        public IEnumerable<PackageDependencyGroup> GetDependencyGroups()
        {
            return GetDependencyGroups(useStrictVersionCheck: false);
        }

        /// <summary>
        /// Read package dependencies for all frameworks
        /// </summary>
        public IEnumerable<PackageDependencyGroup> GetDependencyGroups(bool useStrictVersionCheck)
        {
            var ns = MetadataNode.GetDefaultNamespace().NamespaceName;
            var dependencyNode = MetadataNode
                .Elements(XName.Get(Dependencies, ns));

            var groupFound = false;
            var dependencyGroups = dependencyNode
                .Elements(XName.Get(Group, ns));

            foreach (var depGroup in dependencyGroups)
            {
                groupFound = true;

                var groupFramework = GetAttributeValue(depGroup, TargetFramework);

                var dependencies = depGroup
                    .Elements(XName.Get(Dependency, ns));

                var packages = GetPackageDependencies(dependencies, useStrictVersionCheck);

                var framework = string.IsNullOrEmpty(groupFramework)
                    ? NuGetFramework.AnyFramework
                    : NuGetFramework.Parse(groupFramework, _frameworkProvider);

                yield return new PackageDependencyGroup(framework, packages);
            }

            // legacy behavior
            if (!groupFound)
            {
                var legacyDependencies = dependencyNode
                    .Elements(XName.Get(Dependency, ns));

                var packages = GetPackageDependencies(legacyDependencies, useStrictVersionCheck);

                if (packages.Any())
                {
                    yield return new PackageDependencyGroup(NuGetFramework.AnyFramework, packages);
                }
            }
        }

        /// <summary>
        /// Reference item groups
        /// </summary>
        public IEnumerable<FrameworkSpecificGroup> GetReferenceGroups()
        {
            var ns = MetadataNode.GetDefaultNamespace().NamespaceName;

            var groupFound = false;

            foreach (var group in MetadataNode.Elements(XName.Get(References, ns)).Elements(XName.Get(Group, ns)))
            {
                groupFound = true;

                var groupFramework = GetAttributeValue(group, TargetFramework);

                var items = group.Elements(XName.Get(Reference, ns)).Select(n => GetAttributeValue(n, File)).Where(n => !string.IsNullOrEmpty(n)).ToArray();

                var framework = string.IsNullOrEmpty(groupFramework) ? NuGetFramework.AnyFramework : NuGetFramework.Parse(groupFramework, _frameworkProvider);

                yield return new FrameworkSpecificGroup(framework, items);
            }

            // pre-2.5 flat list of references, this should only be used if there are no groups
            if (!groupFound)
            {
                var items = MetadataNode.Elements(XName.Get(References, ns))
                    .Elements(XName.Get(Reference, ns)).Select(n => GetAttributeValue(n, File)).Where(n => !string.IsNullOrEmpty(n)).ToArray();

                if (items.Length > 0)
                {
                    yield return new FrameworkSpecificGroup(NuGetFramework.AnyFramework, items);
                }
            }

            yield break;
        }

        /// <summary>
        /// Framework reference groups
        /// </summary>
        public IEnumerable<FrameworkSpecificGroup> GetFrameworkReferenceGroups()
        {
            var results = new List<FrameworkSpecificGroup>();

            var ns = Xml.Root.GetDefaultNamespace().NamespaceName;

            var groups = new Dictionary<NuGetFramework, HashSet<string>>(new NuGetFrameworkFullComparer());

            foreach (var group in MetadataNode.Elements(XName.Get(FrameworkAssemblies, ns)).Elements(XName.Get(FrameworkAssembly, ns))
                .GroupBy(n => GetAttributeValue(n, TargetFramework)))
            {
                // Framework references may have multiple comma delimited frameworks
                var frameworks = new List<NuGetFramework>();

                // Empty frameworks go under Any
                if (string.IsNullOrEmpty(group.Key))
                {
                    frameworks.Add(NuGetFramework.AnyFramework);
                }
                else
                {
                    foreach (var fwString in group.Key.Split(CommaArray, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!string.IsNullOrEmpty(fwString))
                        {
                            frameworks.Add(NuGetFramework.Parse(fwString.Trim(), _frameworkProvider));
                        }
                    }
                }

                // apply items to each framework
                foreach (var framework in frameworks)
                {
                    HashSet<string> items = null;
                    if (!groups.TryGetValue(framework, out items))
                    {
                        items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        groups.Add(framework, items);
                    }

                    // Merge items and ignore duplicates
                    items.UnionWith(group.Select(item => GetAttributeValue(item, AssemblyName)).Where(item => !string.IsNullOrEmpty(item)));
                }
            }

            // Sort items to make this deterministic for the caller
            foreach (var framework in groups.Keys.OrderBy(e => e, new NuGetFrameworkSorter()))
            {
                var group = new FrameworkSpecificGroup(framework, groups[framework].OrderBy(item => item, StringComparer.OrdinalIgnoreCase));

                results.Add(group);
            }

            return results;
        }

        /// <summary>
        /// Package language
        /// </summary>
        public string GetLanguage()
        {
            var node = MetadataNode.Elements(XName.Get(Language, MetadataNode.GetDefaultNamespace().NamespaceName)).FirstOrDefault();
            return node?.Value;
        }

        /// <summary>
        /// Package License Url
        /// </summary>
        public string GetLicenseUrl()
        {
            var node = MetadataNode.Elements(XName.Get(LicenseUrl, MetadataNode.GetDefaultNamespace().NamespaceName)).FirstOrDefault();
            return node?.Value;
        }

        /// <summary>
        /// Build action groups
        /// </summary>
        public IEnumerable<ContentFilesEntry> GetContentFiles()
        {
            var ns = MetadataNode.GetDefaultNamespace().NamespaceName;

            foreach (var filesNode in MetadataNode
                .Elements(XName.Get(ContentFiles, ns))
                .Elements(XName.Get(Files, ns)))
            {
                var include = GetAttributeValue(filesNode, "include");

                if (include == null)
                {
                    // Invalid include
                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.InvalidNuspecEntry,
                        filesNode.ToString().Trim(),
                        GetIdentity());

                    throw new PackagingException(message);
                }

                var exclude = GetAttributeValue(filesNode, "exclude");

                if (string.IsNullOrEmpty(exclude))
                {
                    exclude = null;
                }

                var buildAction = GetAttributeValue(filesNode, BuildAction);
                var flatten = AttributeAsNullableBool(filesNode, Flatten);
                var copyToOutput = AttributeAsNullableBool(filesNode, CopyToOutput);

                yield return new ContentFilesEntry(include, exclude, buildAction, copyToOutput, flatten);
            }

            yield break;
        }

        /// <summary>
        /// Package title.
        /// </summary>
        public string GetTitle()
        {
            return GetMetadataValue("title");
        }

        /// <summary>
        /// Package authors.
        /// </summary>
        public string GetAuthors()
        {
            return GetMetadataValue("authors");
        }

        /// <summary>
        /// Package tags.
        /// </summary>
        public string GetTags()
        {
            return GetMetadataValue("tags");
        }

        /// <summary>
        /// Package owners.
        /// </summary>
        public string GetOwners()
        {
            return GetMetadataValue("owners");
        }

        /// <summary>
        /// Package description.
        /// </summary>
        public string GetDescription()
        {
            return GetMetadataValue("description");
        }

        /// <summary>
        /// Package release notes.
        /// </summary>
        public string GetReleaseNotes()
        {
            return GetMetadataValue("releaseNotes");
        }

        /// <summary>
        /// Package summary.
        /// </summary>
        public string GetSummary()
        {
            return GetMetadataValue("summary");
        }

        /// <summary>
        /// Package project url.
        /// </summary>
        public string GetProjectUrl()
        {
            return GetMetadataValue("projectUrl");
        }

        /// <summary>
        /// Package icon url.
        /// </summary>
        public string GetIconUrl()
        {
            return GetMetadataValue("iconUrl");
        }

        /// <summary>
        /// Copyright information.
        /// </summary>
        public string GetCopyright()
        {
            return GetMetadataValue("copyright");
        }

        /// <summary>
        /// Source control repository information.
        /// </summary>
        public RepositoryMetadata GetRepositoryMetadata()
        {
            var repository = new RepositoryMetadata();
            var node = MetadataNode.Elements(XName.Get(Repository, MetadataNode.GetDefaultNamespace().NamespaceName)).FirstOrDefault();

            if (node != null)
            {
                repository.Type = GetAttributeValue(node, "type") ?? string.Empty;
                repository.Url = GetAttributeValue(node, "url") ?? string.Empty;
                repository.Branch = GetAttributeValue(node, "branch") ?? string.Empty;
                repository.Commit = GetAttributeValue(node, "commit") ?? string.Empty;
            }

            return repository;
        }

        //If someone somehow creates a package that has both a LicenseFile and LicenseException, what's the Visual Studio experience.
        //What if the package expression does not parse?
        //What does restore do? What does Visual Studio do? 

        //dotnet pack
        //valid expression
        //invalid expression
        //both expression and LicenseFile
        //license file
        //license file not present on disk
        //license file with an ivnalid extensions(each extensions needs to be valid.)

        //nuspec pack.

        //valid expression
        //invalid expression
        //both expression and LicenseFile
        //license file
        //license file not present on disk
        //license file with an ivnalid extensions (each extensions needs to be valid.)
        //convention based license file inclusion.
        //convention based license file inclusion with duplicates.

        public LicenseMetadata GetLicenseMetadata()
        {
            var ns = MetadataNode.GetDefaultNamespace().NamespaceName;
            var licenseNode = MetadataNode.Elements(XName.Get(License, ns)).FirstOrDefault();

            if (licenseNode != null)
            {
                var type = licenseNode.Attribute(NuspecUtility.Type).Value;
                var license = licenseNode.Value;
                var versionValue = licenseNode.Attribute(NuspecUtility.Version)?.Value;

                var isKnownType = Enum.TryParse(type, true, out LicenseType licenseType);

                if (isKnownType)
                {
                    Version version = null;
                    if (versionValue != null)
                    {
                        if (!System.Version.TryParse(versionValue, out version))
                        {
                            throw new PackagingException(NuGetLogCode.NU5034, string.Format(
                                CultureInfo.CurrentCulture,
                                Strings.License_InvalidLicenseExpressionVersion,
                                versionValue));
                        }
                    }
                    else
                    {
                        version = LicenseMetadata.EmptyVersion;
                    }

                    if (licenseType == LicenseType.Expression)
                    {
                        if (version.CompareTo(LicenseMetadata.CurrentVersion) <= 0)
                        {
                            try
                            {
                                var expression = NuGetLicenseExpression.Parse(license);
                                return new LicenseMetadata(licenseType, license, expression, version);
                            }
                            catch (NuGetLicenseExpressionParsingException e) // TODO NK - Validate that the internal message is actually validated. Validate the scenario where the version is higher than the nuspec reader can understand.
                            {
                                throw new PackagingException(NuGetLogCode.NU5032, e.Message, e);
                            }
                        }
                        else
                        {
                            return new LicenseMetadata(licenseType, license, null, version);
                        }
                    }
                    return new LicenseMetadata(licenseType, license, null, LicenseMetadata.EmptyVersion);
                }
            }
            return null;
        }

        /// <summary>
        /// Require license acceptance when installing the package.
        /// </summary>
        public bool GetRequireLicenseAcceptance()
        {
            return StringComparer.OrdinalIgnoreCase.Equals(bool.TrueString, GetMetadataValue("requireLicenseAcceptance"));
        }

        private static bool? AttributeAsNullableBool(XElement element, string attributeName)
        {
            bool? result = null;

            var attributeValue = GetAttributeValue(element, attributeName);

            if (attributeValue != null)
            {
                if (bool.TrueString.Equals(attributeValue, StringComparison.OrdinalIgnoreCase))
                {
                    result = true;
                }
                else if (bool.FalseString.Equals(attributeValue, StringComparison.OrdinalIgnoreCase))
                {
                    result = false;
                }
                else
                {
                    var message = string.Format(
                            CultureInfo.CurrentCulture,
                            Strings.InvalidNuspecEntry,
                            element.ToString().Trim());

                    throw new PackagingException(message);
                }
            }

            return result;
        }

        private static string GetAttributeValue(XElement element, string attributeName)
        {
            var attribute = element.Attribute(XName.Get(attributeName));
            return attribute == null ? null : attribute.Value;
        }

        private static readonly List<string> EmptyList = new List<string>();

        private static List<string> GetFlags(string flags)
        {
            if (string.IsNullOrEmpty(flags))
            {
                return EmptyList;
            }

            var set = new HashSet<string>(
                flags.Split(CommaArray, StringSplitOptions.RemoveEmptyEntries)
                    .Select(flag => flag.Trim()),
                StringComparer.OrdinalIgnoreCase);

            return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private HashSet<PackageDependency> GetPackageDependencies(IEnumerable<XElement> nodes, bool useStrictVersionCheck)
        {
            var packages = new HashSet<PackageDependency>();

            foreach (var depNode in nodes)
            {
                VersionRange range = null;

                var rangeNode = GetAttributeValue(depNode, Version);

                if (!string.IsNullOrEmpty(rangeNode))
                {
                    var versionParsedSuccessfully = VersionRange.TryParse(rangeNode, out range);
                    if (!versionParsedSuccessfully && useStrictVersionCheck)
                    {
                        // Invalid version
                        var dependencyId = GetAttributeValue(depNode, Id);
                        var message = string.Format(
                            CultureInfo.CurrentCulture,
                            Strings.ErrorInvalidPackageVersionForDependency,
                            dependencyId,
                            GetIdentity(),
                            rangeNode);

                        throw new PackagingException(message);
                    }
                }
                else if (useStrictVersionCheck)
                {
                    // Invalid version
                    var dependencyId = GetAttributeValue(depNode, Id);
                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        Strings.ErrorInvalidPackageVersionForDependency,
                        dependencyId,
                        GetIdentity(),
                        rangeNode);

                    throw new PackagingException(message);
                }

                var includeFlags = GetFlags(GetAttributeValue(depNode, IncludeFlags));
                var excludeFlags = GetFlags(GetAttributeValue(depNode, ExcludeFlags));

                var dependency = new PackageDependency(
                    GetAttributeValue(depNode, Id),
                    range,
                    includeFlags,
                    excludeFlags);

                packages.Add(dependency);
            }

            return packages;
        }
    }
}
