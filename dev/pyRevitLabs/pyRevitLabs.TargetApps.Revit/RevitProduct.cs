﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

using Microsoft.Win32;

using pyRevitLabs.Common;
using pyRevitLabs.Common.Extensions;
using pyRevitLabs.NLog;

namespace pyRevitLabs.TargetApps.Revit {
    public class HostProductInfoMeta {
        public string schema { get; set; } = "1.0";
        public string source { get; set; }
    }

    public class HostProductInfo {
        public HostProductInfoMeta meta { get; set; }
        public string name { get; set; }
        public string version { get; set; }
        public string build { get; set; }
        public string target { get; set; }
    }

    public class RevitProductData {
        private const string SourceFileName = "pyrevit-hosts.json";
        private static Regex BuildNumberFinder = new Regex(@".*(?<build>\d{8}_\d{4}).*");
        private static Regex BuildTargetFinder = new Regex(@".*\((?<target>[xX]\d{2})\).*");

        public static string ExtractBuildNumberFromString(string inputString) {
            Match match = BuildNumberFinder.Match(inputString);
            if (match.Success)
                return match.Groups["build"].Value;
            return string.Empty;
        }

        public static string ExtractBuildTargetFromString(string inputString) {
            Match match = BuildTargetFinder.Match(inputString);
            if (match.Success)
                return match.Groups["target"].Value;
            return string.Empty;
        }

        public static HostProductInfo GetProductInfo(string identifier) {
            // identifier can be build, version, or name (any of the properties in the data set
            // check if the string has build number e.g. "20110309_2315"
            var buildNumber = ExtractBuildNumberFromString(identifier);
            if (buildNumber != string.Empty)
                identifier = buildNumber;

            identifier = identifier.ToLower();
            foreach (HostProductInfo prodInfo in GetAllProductInfo()) {
                if (prodInfo.meta.schema == "1.0") {
                    if (prodInfo.name.ToLower() == identifier
                        || prodInfo.version.ToLower() == identifier
                        || prodInfo.build.ToLower() == identifier)
                        return prodInfo;
                }
            }
            return null;
        }

        public static List<HostProductInfo> GetAllProductInfo() {
            var hostInfoDataSet = File.ReadAllText(
                Path.Combine(CommonUtils.GetAssemblyPath<RevitProductData>(), SourceFileName)
                );
            return new JavaScriptSerializer().Deserialize<List<HostProductInfo>>(hostInfoDataSet);
        }

        public static string GetBinaryLocation(string installPath) {
            var possibleLocations = new List<string>() {
                Path.Combine(installPath, "Revit.exe"),
                Path.Combine(installPath, "Program", "Revit.exe")
            };
            foreach (var binaryLoc in possibleLocations)
                if (File.Exists(binaryLoc))
                    return binaryLoc;
            return null;
        }

        public static int GetProductYear(string inputString) {
            if (inputString != null) {
                var productYearFinder = new Regex(@".*\s+(?<product_year>\d{4}).*");
                var match = productYearFinder.Match(inputString);
                if (match.Success) {
                    var productYear = match.Groups["product_year"].Value;
                    return int.Parse(productYear);
                }
            }
            // if product year not found, return 0
            return 0;
        }

        public static int GetProductYear(Version version) {
            if (version.Major < 100)
                return 2000 + version.Major;
            return version.Major;
        }

        public static HostProductInfo GetBinaryProductInfo(string binaryPath) {
            var fileInfo = FileVersionInfo.GetVersionInfo(binaryPath);
            return new HostProductInfo {
                // attempt at creating a nice name, based on version
                name = string.Format("{0} 20{1}", fileInfo.ProductName, fileInfo.FileVersion.Substring(0, 2)),
                version = fileInfo.FileVersion,
                build = ExtractBuildNumberFromString(fileInfo.ProductVersion),
                target = ExtractBuildTargetFromString(fileInfo.ProductVersion)
            };
        }
    }

    public class RevitProduct {
        private string _registeredName = string.Empty;
        private string _registeredInstallPath = string.Empty;

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private RevitProduct(HostProductInfo prodInfo) {
            Name = prodInfo.name;
            Version = new Version(prodInfo.version);
            BuildNumber = prodInfo.build;
            BuildTarget = prodInfo.target;
        }

        public override string ToString() {
            return string.Format("{0} | Version: {1} | Build: {2}({3}) | Language: {4} | Path: \"{5}\"", Name, Version, BuildNumber, BuildTarget, LanguageCode, InstallLocation);
        }

        public override int GetHashCode() {
            return BuildNumber.GetHashCode();
        }

        public string Name { get; private set; }
        public int ProductYear {
            get {
                int prodYear = 0;
                prodYear = RevitProductData.GetProductYear(Name);
                if (prodYear == 0)
                    prodYear = RevitProductData.GetProductYear(Version);
                return prodYear;
            }
        }
        public Version Version { get; private set; }
        public string BuildNumber { get; private set; }
        public string BuildTarget { get; private set; }

        public string DefaultInstallLocation {
            get {
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }
        }
        public string InstallLocation {
            get {
                if (_registeredInstallPath is null || _registeredInstallPath == string.Empty) {
                    string revitInstallDirName = string.Empty;
                    if (ProductYear != 0)
                        revitInstallDirName = string.Format("Revit {0}", ProductYear);

                    if (revitInstallDirName != string.Empty) {
                        var expectedPath = Path.Combine(DefaultInstallLocation, "Autodesk", revitInstallDirName);
                        logger.Debug("Expected path {0}", expectedPath);
                        if (CommonUtils.VerifyPath(expectedPath))
                            return expectedPath;
                    }
                }

                return _registeredInstallPath;
            }

            set {
                if (value != null)
                    _registeredInstallPath = value;
            }
        }
        public string ExecutiveLocation => RevitProductData.GetBinaryLocation(InstallLocation);
        public int LanguageCode { get; set; }

        // static:
        public static RevitProduct LookupRevitProduct(string buildOrVersionString) {
            var prodInfo = RevitProductData.GetProductInfo(buildOrVersionString);
            if (prodInfo != null)
                return new RevitProduct(prodInfo);
            return null;
        }

        public static RevitProduct LookupRevitProduct(Version version) {
            return LookupRevitProduct(version.ToString());
        }

        public static List<RevitProduct> ListInstalledProducts() {
            var installedRevits = new HashSet<RevitProduct>();

            // pattern for finding revit installation entries in registry
            // matching:
            //     Revit 2019
            //     Revit 2019 - German
            //     Revit Architecture 2016 - Imperial
            // fails:
            //     Revit Content Libraries 2016
            var revitFinder = new Regex(@"^Revit\s[A-Za-z]*\s*\d{4}\s?($|\s-)");

            // open parent regkey
            var uninstallKey =
                Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            // loop thru subkeys and find matching
            foreach (var key in uninstallKey.GetSubKeyNames()) {
                var subkey = uninstallKey.OpenSubKey(key);
                var appName = subkey.GetValue("DisplayName") as string;
                logger.Debug("Analysing registered app: {0}", appName);
                if (appName != null && revitFinder.IsMatch(appName)) {
                    logger.Debug("App is a Revit product: {0}", appName);
                    try {
                        // collect info from reg key
                        var regName = subkey.GetValue("DisplayName") as string;
                        var regVersion = subkey.GetValue("DisplayVersion") as string;
                        var regPath = (subkey.GetValue("InstallLocation") as string).NormalizeAsPath();
                        int regLangCode = (int)subkey.GetValue("Language");
                        logger.Debug("Version from registery key: \"{0}\"", regVersion);
                        logger.Debug("Install path from registery key: \"{0}\"", regPath);
                        logger.Debug("Language code from registery key: \"{0}\"", regLangCode);

                        // attempt at finding revit product
                        RevitProduct revitProduct = null;
                        revitProduct = LookupRevitProduct(regVersion);
                        // if could not determine product by version
                        if (revitProduct is null) {
                            logger.Debug("Could not determine Revit Product from version \"{0}\"", regVersion);
                            // try to get product key from binary version
                            var binaryLocation = RevitProductData.GetBinaryLocation(regPath);
                            if (binaryLocation != null) {
                                try {
                                    var prodInfo = RevitProductData.GetBinaryProductInfo(binaryLocation);
                                    logger.Debug("Read build number \"{0}\" from binary at \"{1}\"", prodInfo.build, binaryLocation);
                                    revitProduct = LookupRevitProduct(prodInfo.build);
                                    if (revitProduct is null) {
                                        // revit info might not be available specially if it is new
                                        // lets build a RevitProduct with whatever info we could collect
                                        revitProduct = new RevitProduct(prodInfo);
                                    }
                                }
                                catch {
                                    logger.Debug("Failed reading product info from binary at \"{0}\"", binaryLocation);
                                }
                            }
                        }

                        logger.Debug("Revit Product is : {0}", revitProduct);
                        if (revitProduct != null) {
                            // grab the registery name if it doesn't have a name
                            if (revitProduct.Name is null || revitProduct.Name == string.Empty)
                                revitProduct.Name = regName;

                            // build from a registry version if it doesn't already have one
                            if (revitProduct.Version is null && (regVersion != null && regVersion != string.Empty))
                                revitProduct.Version = new Version(regVersion);
                            // update install path from registry if it can't find one
                            if (revitProduct.InstallLocation is null && (regPath != null && regPath != string.Empty))
                                revitProduct.InstallLocation = regPath;

                            // this can only come from registrys
                            revitProduct.LanguageCode = regLangCode;

                            // add to list now
                            installedRevits.Add(revitProduct);
                        }
                        else {
                            logger.Debug("Can not determine Revit product.");
                        }
                    }
                    catch (Exception rpEx) {
                        logger.Error("Error determining installed Revit Product from: {0} | {1} | {2}",
                                     appName, rpEx.Message, rpEx.InnerException.Message);
                    }
                }
            }

            return installedRevits.ToList();
        }

        public static List<RevitProduct> ListSupportedProducts() {
            var installedRevits = new HashSet<RevitProduct>();
            foreach (var regProduct in RevitProductData.GetAllProductInfo())
                installedRevits.Add(new RevitProduct(regProduct));
            return installedRevits.ToList();
        }
    }
}