using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Microsoft.Deployment.WindowsInstaller;
using Microsoft.Deployment.WindowsInstaller.Linq;
using Microsoft.Msix.Utils;
using Microsoft.Msix.Utils.AppxPackaging;
using Microsoft.Msix.Utils.AppxPackagingInterop;
using Newtonsoft.Json;
using Vestris.ResourceLib;

namespace PackageParse
{
    public static class PackageParser
    {
        private const string InvalidCharacters = "©|®";

        private static readonly string[] KnownInstallerResourceNames = new[]
        {
            "inno",
            "nullsoft",
        };

        private enum MachineType
        {
            X86 = 0x014c,
            X64 = 0x8664,
            Arm = 0x01c0,
            Armv7 = 0x01c4,
            Arm64 = 0xaa64,
        }

        public static string GetFileHash(string path)
        {
            using Stream stream = File.OpenRead(path);
            using var hasher = SHA256.Create();
            return BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", "");
        }

        public static PackageInfo ParsePackage(string packagePath)
        {
            if (!File.Exists(packagePath))
            {
                throw new FileNotFoundException($"Package file not found: {packagePath}");
            }

            var packageInfo = new PackageInfo
            {
                FilePath = packagePath,
                FileSize = new FileInfo(packagePath).Length,
                FileHash = GetFileHash(packagePath)
            };

            var versionInfo = FileVersionInfo.GetVersionInfo(packagePath);
            
            packageInfo.BasicInfo = new BasicPackageInfo
            {
                PackageName = versionInfo.ProductName?.Trim(),
                PackageVersion = versionInfo.FileVersion?.Trim() ?? versionInfo.ProductVersion?.Trim(),
                Publisher = versionInfo.CompanyName?.Trim(),
                ShortDescription = versionInfo.FileDescription?.Trim(),
                Copyright = versionInfo.LegalCopyright?.Trim()
            };

            var installers = new List<InstallerInfo>();
            
            if (ParseExeInstallerType(packagePath, out var exeInstaller))
            {
                installers.Add(exeInstaller);
            }
            else if (ParseMsi(packagePath, out var msiInstaller))
            {
                installers.Add(msiInstaller);
            }
            else if (ParseMsix(packagePath, out var msixInstallers))
            {
                installers.AddRange(msixInstallers);
            }
            else if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseZip(packagePath, out var zipInstaller))
                {
                    installers.Add(zipInstaller);
                }
                else
                {
                    installers.Add(new InstallerInfo
                    {
                        Architecture = "Neutral",
                        InstallerType = "zip",
                        InstallerSha256 = packageInfo.FileHash
                    });
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported package format: {packagePath}");
            }

            packageInfo.Installers = installers;
            return packageInfo;
        }

        private static bool ParseExeInstallerType(string path, out InstallerInfo installerInfo)
        {
            installerInfo = null;

            try
            {
                ManifestResource rc = new ManifestResource();
                string installerTypeStr;

                try
                {
                    rc.LoadFrom(path);
                    string installerType = rc.Manifest.DocumentElement
                        .GetElementsByTagName("description")
                        .Cast<XmlNode>()
                        .FirstOrDefault()?
                        .InnerText?
                        .Split(' ').First()
                        .ToLowerInvariant();

                    if (installerType.EqualsIC("wix"))
                    {
                        installerTypeStr = "Burn";
                    }
                    else if (KnownInstallerResourceNames.Contains(installerType))
                    {
                        installerTypeStr = installerType;
                    }
                    else
                    {
                        installerTypeStr = "exe";
                    }
                }
                catch (Win32Exception err)
                {
                    if ((err.Message == "The specified resource type cannot be found in the image file."
                        && err.NativeErrorCode == 1813) ||
                        (err.Message == "The specified image file did not contain a resource section."
                        && err.NativeErrorCode == 1812))
                    {
                        installerTypeStr = "exe";
                    }
                    else
                    {
                        return false;
                    }
                }

                var machineType = GetMachineType(path);
                string architecture = machineType?.ToString() ?? "Neutral";

                installerInfo = new InstallerInfo
                {
                    Architecture = architecture,
                    InstallerType = installerTypeStr,
                    InstallerSha256 = GetFileHash(path)
                };

                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        private static bool ParseMsi(string path, out InstallerInfo installerInfo)
        {
            installerInfo = null;

            try
            {
                using (var database = new QDatabase(path, Microsoft.Deployment.WindowsInstaller.DatabaseOpenMode.ReadOnly))
                {
                    var properties = database.Properties.ToList();

                    string archString = database.SummaryInfo.Template.Split(';').First();

                    archString = archString.EqualsIC("Intel") ? "x86" :
                        archString.EqualsIC("Intel64") ? "x64" :
                        archString.EqualsIC("Arm64") ? "Arm64" :
                        archString.EqualsIC("Arm") ? "Arm" : archString;

                    installerInfo = new InstallerInfo
                    {
                        Architecture = archString,
                        InstallerType = IsWix(database) ? "Wix" : "Msi",
                        InstallerSha256 = GetFileHash(path),
                        ProductCode = properties.FirstOrDefault(p => p.Property == "ProductCode")?.Value
                    };

                    return true;
                }
            }
            catch (Microsoft.Deployment.WindowsInstaller.InstallerException)
            {
                return false;
            }
        }

        private static bool ParseMsix(string path, out List<InstallerInfo> installerInfos)
        {
            installerInfos = new List<InstallerInfo>();

            try
            {
                var appxMetadatas = new List<AppxMetadata>();
                string signatureSha256;

                try
                {
                    var bundle = new AppxBundleMetadata(path);
                    IAppxFile signatureFile = bundle.AppxBundleReader.GetFootprintFile(APPX_BUNDLE_FOOTPRINT_FILE_TYPE.APPX_BUNDLE_FOOTPRINT_FILE_TYPE_SIGNATURE);
                    signatureSha256 = HashAppxFile(signatureFile);

                    foreach (var childPackage in bundle.ChildAppxPackages.Where(p => p.PackageType == PackageType.Application))
                    {
                        if (childPackage.RelativeFilePath.StartsWith("AppxMetadata\\Stub", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var appxFile = bundle.AppxBundleReader.GetPayloadPackage(childPackage.RelativeFilePath);
                        appxMetadatas.Add(new AppxMetadata(appxFile.GetStream()));
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    var appxMetadata = new AppxMetadata(path);
                    appxMetadatas.Add(appxMetadata);
                    IAppxFile signatureFile = appxMetadata.AppxReader.GetFootprintFile(APPX_FOOTPRINT_FILE_TYPE.APPX_FOOTPRINT_FILE_TYPE_SIGNATURE);
                    signatureSha256 = HashAppxFile(signatureFile);
                }

                foreach (var appxMetadata in appxMetadatas)
                {
                    var installerInfo = new InstallerInfo
                    {
                        Architecture = appxMetadata.Architecture?.ToString() ?? "Neutral",
                        InstallerType = "Msix",
                        InstallerSha256 = GetFileHash(path),
                        SignatureSha256 = signatureSha256,
                        PackageFamilyName = appxMetadata.PackageFamilyName,
                        MinimumOSVersion = appxMetadata.MinOSVersion?.ToString()
                    };

                    installerInfos.Add(installerInfo);
                }

                return installerInfos.Any();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }

        private static bool ParseZip(string path, out InstallerInfo? installerInfo)
        {
            installerInfo = null;
            try
            {
                using var archive = ZipFile.OpenRead(path);
                // 尝试在zip中查找可执行文件或MSI
                var entry = archive.Entries.FirstOrDefault(e => 
                    e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || 
                    e.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase));

                if (entry != null)
                {
                    string tempFile = Path.Combine(Path.GetTempPath(), entry.Name);
                    entry.ExtractToFile(tempFile, true);
                    try
                    {
                        if (entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            ParseExeInstallerType(tempFile, out var innerInfo);
                            installerInfo = innerInfo;
                        }
                        else if (entry.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            ParseMsi(tempFile, out var innerInfo);
                            installerInfo = innerInfo;
                        }
                        
                        if (installerInfo != null)
                        {
                            installerInfo.InstallerType = "zip"; // 根类型是zip
                            installerInfo.InstallerSha256 = GetFileHash(path);
                            return true;
                        }
                    }
                    finally
                    {
                        File.Delete(tempFile);
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsWix(QDatabase installer)
        {
            return
                installer.Tables.AsEnumerable().Any(table => table.Name.ToLower().Contains("wix")) ||
                installer.Properties.AsEnumerable().Any(property => property.Property.ToLower().Contains("wix") || property.Value.ToLower().Contains("wix")) ||
                installer.SummaryInfo.CreatingApp.ToLower().Contains("wix") ||
                installer.SummaryInfo.CreatingApp.ToLower().Contains("windows installer xml");
        }

        private static MachineType? GetMachineType(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = br.ReadInt32();
                fs.Seek(peOffset, SeekOrigin.Begin);
                uint peHeader = br.ReadUInt32();

                if (peHeader != 0x00004550) // "PE\0\0"
                {
                    return null;
                }

                ushort machine = br.ReadUInt16();

                if (Enum.IsDefined(typeof(MachineType), machine))
                {
                    return (MachineType)machine;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string HashAppxFile(IAppxFile file)
        {
            var comStream = file.GetStream();
            using var hasher = SHA256.Create();
            
            // 从IStream读取数据
            const int bufferSize = 4096;
            var buffer = new byte[bufferSize];
            int bytesRead;
            
            // 重置流位置 - IStream.Seek需要3个参数
            comStream.Seek(0, 0, IntPtr.Zero);
            
            while ((bytesRead = ReadFromIStream(comStream, buffer, bufferSize)) > 0)
            {
                hasher.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            
            hasher.TransformFinalBlock(buffer, 0, 0);
            return BitConverter.ToString(hasher.Hash).Replace("-", "");
        }

        private static int ReadFromIStream(System.Runtime.InteropServices.ComTypes.IStream stream, byte[] buffer, int count)
        {
            IntPtr bytesReadPtr = IntPtr.Zero;
            try
            {
                bytesReadPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(int)));
                stream.Read(buffer, count, bytesReadPtr);
                return Marshal.ReadInt32(bytesReadPtr);
            }
            finally
            {
                if (bytesReadPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(bytesReadPtr);
                }
            }
        }

        private static bool EqualsIC(this string str, string other)
        {
            return string.Equals(str, other, StringComparison.OrdinalIgnoreCase);
        }

        private static T? ToEnumOrDefault<T>(this string value) where T : struct, Enum
        {
            if (Enum.TryParse<T>(value, true, out T result))
            {
                return result;
            }
            return null;
        }
    }

    public class PackageInfo
    {
        public string? FilePath { get; set; }
        public long FileSize { get; set; }
        public string? FileHash { get; set; }
        public BasicPackageInfo? BasicInfo { get; set; }
        public List<InstallerInfo> Installers { get; set; } = new List<InstallerInfo>();
    }

    public class BasicPackageInfo
    {
        public string? PackageName { get; set; }
        public string? PackageVersion { get; set; }
        public string? Publisher { get; set; }
        public string? ShortDescription { get; set; }
        public string? Copyright { get; set; }
    }

    public class InstallerInfo
    {
        public string? Architecture { get; set; }
        public string? InstallerType { get; set; }
        public string? InstallerSha256 { get; set; }
        public string? SignatureSha256 { get; set; }
        public string? ProductCode { get; set; }
        public string? PackageFamilyName { get; set; }
        public string? MinimumOSVersion { get; set; }
        public List<string>? Platform { get; set; }
        public string? Scope { get; set; }
        public string? InstallerLocale { get; set; }
    }
}