﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Fx.Portability.ObjectModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;

namespace Microsoft.Fx.Portability.Analyzer
{
    internal class DependencyFinderEngineHelper
    {
        private readonly MetadataReader _reader;
        public AssemblyInfo CallingAssembly { get; private set; }
        public List<MemberDependency> memberDependency = new List<MemberDependency>();
        private string _currentAssemblyInfo;
        private string _currentAssemblyName;
        private string _assemblyInfoForPrimitives = null;
        private string _assemblyLocation;
        public DependencyFinderEngineHelper(MetadataReader metadataReader, string assemblyPath)
        {
            _reader = metadataReader;
            _assemblyLocation = assemblyPath;
        }

        public void ComputeData()
        {
            if (_reader != null)
            {
                //get assembly info
                CallingAssembly = GetAssemblyInfo();
                _currentAssemblyInfo = GetCurrentAssemblyInfo();

                //get type references
                foreach (var handle in _reader.TypeReferences)
                {
                    var entry = _reader.GetTypeReference(handle);

                    MemberDependency dep = GetTypeReferenceMemberDependency(entry);
                    if (dep != null)
                        memberDependency.Add(dep);
                }


                //get member references
                foreach (var handle in _reader.MemberReferences)
                {
                    var entry = _reader.GetMemberReference(handle);

                    MemberDependency dep = GetMemberReferenceMemberDependency(entry);
                    if (dep != null)
                        memberDependency.Add(dep);
                }
            }
        }

        private AssemblyInfo GetAssemblyInfo()
        {
            AssemblyInfo assemblyInfo = new AssemblyInfo();
            Assembly assembly = Assembly.ReflectionOnlyLoadFrom(_assemblyLocation);

            foreach (var referencedAssemblyName in assembly.GetReferencedAssemblies())
            {
                try
                {
                    Assembly.ReflectionOnlyLoad(referencedAssemblyName.FullName);
                }
                catch
                {
                    Assembly.ReflectionOnlyLoadFrom(Path.Combine(Path.GetDirectoryName(_assemblyLocation), referencedAssemblyName.Name + ".dll"));
                }
            }

            AssemblyName assemblyName = assembly.GetName();

            //get file version
            FileVersionInfo fileInfo = FileVersionInfo.GetVersionInfo(_assemblyLocation);
            assemblyInfo.FileVersion = fileInfo.FileVersion;
            assemblyInfo.AssemblyIdentity = assemblyName.ToString();

            //get target framework from custom attributes
            var customAttributes = assembly.CustomAttributes;
            string targetFramework = "";
            foreach (CustomAttributeData attribute in customAttributes)
            {
                string name = attribute.AttributeType.Name;
                if (attribute.AttributeType.Name.Contains("TargetFrameworkAttribute"))
                {
                    for (int i = 0; i < attribute.ConstructorArguments.Count; i++)
                    {
                        if (i > 0)
                            targetFramework += ", ";
                        targetFramework += attribute.ConstructorArguments[i].ToString().Trim(new char[] { '\"' });
                    }
                    break;
                }
            }
            assemblyInfo.TargetFrameworkMoniker = targetFramework;
            return assemblyInfo;
        }

        private MemberDependency GetTypeReferenceMemberDependency(TypeReference typeReference)
        {
            MemberMetadataInfo typeRefinfo = MemberMetadataInfo.GetFullName(typeReference, _reader);
            return CreateMemberDependency(typeRefinfo);
        }

        private MemberDependency CreateMemberDependency(MemberMetadataInfo type)
        {
            MemberDependency dep = new MemberDependency();
            dep.CallingAssembly = CallingAssembly;

            if (type.IsPrimitiveType && _assemblyInfoForPrimitives == null)
            {
                if (type.AssemblySet)
                    _assemblyInfoForPrimitives = GetAssemblyInfoFromHandle(type.DefinedInAssembly);
                else if (_currentAssemblyName.ToLower().CompareTo("mscorlib") == 0) //so that we can test on mscorlib
                    _assemblyInfoForPrimitives = _currentAssemblyInfo;
            }

            dep.MemberDocId = "T:" + type.ToString(); ;

            if (type.AssemblySet)
                dep.DefinedInAssemblyIdentity = GetAssemblyInfoFromHandle(type.DefinedInAssembly);
            else
                dep.DefinedInAssemblyIdentity = _currentAssemblyInfo;

            return dep;
        }

        private MemberDependency GetMemberReferenceMemberDependency(MemberReference memberReference)
        {
            MemberDependency dep = new MemberDependency();
            MemberMetadataInfo memberRefInfo = MemberMetadataInfo.GetMemberRefInfo(memberReference, _reader);

            //add the parent type to the types list (only needed when we want to report memberrefs defined in the current assembly)
            if (memberRefInfo.ParentType.IsTypeDef || (memberRefInfo.ParentType.IsPrimitiveType && _currentAssemblyName.Equals("mscorlib", StringComparison.InvariantCultureIgnoreCase)))
                memberDependency.Add(CreateMemberDependency(memberRefInfo.ParentType));

            dep.CallingAssembly = CallingAssembly;

            //MemberReferenceKind can be Method or Field
            string kind;
            switch (memberReference.GetKind())
            {
            case MemberReferenceKind.Field:
                kind = "F:";
                break;
            case MemberReferenceKind.Method:
                kind = "M:";
                break;
            default:
                kind = memberReference.GetKind().ToString();
                break;
            }

            dep.MemberDocId = kind + memberRefInfo.ToString();
            dep.TypeDocId = "T:" + memberRefInfo.ParentType.ToString();

            if (memberRefInfo.ParentType.AssemblySet)
                dep.DefinedInAssemblyIdentity = GetAssemblyInfoFromHandle(memberRefInfo.ParentType.DefinedInAssembly);
            else if (memberRefInfo.ParentType.IsPrimitiveType)  //if it is primitive type, the assembly is not set
                dep.DefinedInAssemblyIdentity = _assemblyInfoForPrimitives;
            else
                dep.DefinedInAssemblyIdentity = _currentAssemblyInfo;

            return dep;
        }

        private string GetAssemblyInfoFromHandle(AssemblyReferenceHandle assemblyHandle)
        {
            AssemblyReference entry = _reader.GetAssemblyReference(assemblyHandle);
            return FormatAssemblyInfo(_reader.GetString(entry.Name), entry.Culture, entry.PublicKeyOrToken, entry.Version);
        }

        private string GetCurrentAssemblyInfo()
        {
            AssemblyDefinition entry = _reader.GetAssemblyDefinition();
            _currentAssemblyName = _reader.GetString(entry.Name);
            return FormatAssemblyInfo(_currentAssemblyName, entry.Culture, entry.PublicKey, entry.Version);
        }

        private string FormatAssemblyInfo(string name, StringHandle cultureHandle, BlobHandle publicKeyTokenHandle, Version version)
        {
            string culture = "neutral";
            if (!cultureHandle.IsNil)
                culture = _reader.GetString(cultureHandle);

            string publicKeyToken = "null";
            if (!publicKeyTokenHandle.IsNil)
                publicKeyToken = FormatPublicKeyToken(publicKeyTokenHandle);
            return name + ", Version=" + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision + ", Culture=" + culture + ", PublicKeyToken=" + publicKeyToken;
        }

        private string FormatPublicKeyToken(BlobHandle handle)
        {
            byte[] bytes = _reader.GetBlobBytes(handle);
            if (bytes == null || bytes.Length <= 0)
                return "null";
            if (bytes.Length > 8)  //strong named assembly
            {
                //get the public key token, which is the last 8 bytes of the SHA-1 hash of the public key 
                SHA1 sha1 = SHA1.Create();
                byte[] token = sha1.ComputeHash(bytes);
                bytes = new byte[8];
                int count = 0;
                for (int i = token.Length - 1; i >= token.Length - 8; i--)
                {
                    bytes[count] = token[i];
                    count++;
                }
            }
            string value = BitConverter.ToString(bytes);

            //remove dashes
            string removeDashesLowerCase = value.Replace("-", "").ToLowerInvariant();
            return string.Format("{0:x}", removeDashesLowerCase);
        }
    }
}
