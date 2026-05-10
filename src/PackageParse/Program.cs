using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PackageParse
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return 1;
            }

            // 检查是否有--help参数
            if (args.Contains("--help"))
            {
                ShowHelp();
                return 0;
            }

            string packagePath = args[0];
            string format = "json";
            bool pretty = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--format":
                        if (i + 1 < args.Length)
                        {
                            format = args[++i].ToLower();
                            if (format != "json" && format != "yaml")
                            {
                                Console.WriteLine($"Error: Invalid format '{format}'. Supported formats: json, yaml");
                                return 1;
                            }
                        }
                        break;
                    case "--pretty":
                        pretty = true;
                        break;
                    default:
                        Console.WriteLine($"Error: Unknown option '{args[i]}'");
                        Console.WriteLine("Use --help for usage information");
                        return 1;
                }
            }

            if (!File.Exists(packagePath))
            {
                Console.WriteLine($"Error: Package file not found: {packagePath}");
                return 1;
            }

            try
            {
                var result = PackageParser.ParsePackage(packagePath);
                
                if (format == "json")
                {
                    var settings = new JsonSerializerSettings
                    {
                        Formatting = pretty ? Formatting.Indented : Formatting.None,
                        NullValueHandling = NullValueHandling.Ignore
                    };
                    Console.WriteLine(JsonConvert.SerializeObject(result, settings));
                }
                else
                {
                    Console.WriteLine(ConvertToYaml(result));
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static string ConvertToYaml(PackageInfo packageInfo)
        {
            var lines = new List<string>
            {
                $"FilePath: {packageInfo.FilePath ?? "null"}",
                $"FileSize: {packageInfo.FileSize}",
                $"FileHash: {packageInfo.FileHash ?? "null"}",
                "",
                "BasicInfo:"
            };

            if (packageInfo.BasicInfo != null)
            {
                lines.Add($"  PackageName: {packageInfo.BasicInfo.PackageName ?? "null"}");
                lines.Add($"  PackageVersion: {packageInfo.BasicInfo.PackageVersion ?? "null"}");
                lines.Add($"  Publisher: {packageInfo.BasicInfo.Publisher ?? "null"}");
                lines.Add($"  ShortDescription: {packageInfo.BasicInfo.ShortDescription ?? "null"}");
                lines.Add($"  Copyright: {packageInfo.BasicInfo.Copyright ?? "null"}");
            }
            else
            {
                lines.Add("  null");
            }

            lines.Add("");
            lines.Add("Installers:");

            foreach (var installer in packageInfo.Installers)
            {
                lines.Add($"  - Architecture: {installer.Architecture ?? "null"}");
                lines.Add($"    InstallerType: {installer.InstallerType ?? "null"}");
                lines.Add($"    InstallerSha256: {installer.InstallerSha256 ?? "null"}");
                lines.Add($"    SignatureSha256: {installer.SignatureSha256 ?? "null"}");
                lines.Add($"    ProductCode: {installer.ProductCode ?? "null"}");
                lines.Add($"    PackageFamilyName: {installer.PackageFamilyName ?? "null"}");
                lines.Add($"    MinimumOSVersion: {installer.MinimumOSVersion ?? "null"}");
                
                if (installer.Platform != null && installer.Platform.Any())
                {
                    lines.Add($"    Platform: [{string.Join(", ", installer.Platform)}]");
                }
                else
                {
                    lines.Add($"    Platform: null");
                }
                
                lines.Add($"    Scope: {installer.Scope ?? "null"}");
                lines.Add($"    InstallerLocale: {installer.InstallerLocale ?? "null"}");
                lines.Add("");
            }

            return string.Join(Environment.NewLine, lines);
        }

        static void ShowHelp()
        {
            Console.WriteLine("Usage: packageparse.exe <package-path> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --format <json|yaml>   Output format (default: json)");
            Console.WriteLine("  --pretty               Pretty print output");
            Console.WriteLine("  --help                 Show this help message");
        }
    }
}
