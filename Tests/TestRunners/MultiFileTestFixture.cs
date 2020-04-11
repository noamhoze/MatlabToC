﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.CommandLine;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.Threading;
using Xunit;
using static ICSharpCode.CodeConverter.CommandLine.CodeConvProgram;
using SearchOption = System.IO.SearchOption;

namespace ICSharpCode.CodeConverter.Tests.TestRunners
{
    /// <summary>
    /// For all files in the testdata folder relevant to the testname, ensures they match the result of the conversion.
    /// Any extra files generated by the conversion are ignored.
    ///
    /// To add a new multi-file characterization test:
    /// 1. Open TestData\MultiFileCharacterization\SourceFiles\CharacterizationTestSolution.sln in another Visual Studio instance and make any changes to the source files.
    /// 2. Set _writeNewCharacterization to true
    /// 3. Run the MultiFileSolutionAndProjectTests for both VB and CSharp.
    /// 4. Set _writeNewCharacterization to false
    /// 5. Commit the result
    /// If you're testing something specific, try to make it clear with a well-named method/class/file or by adding comments in the source file.
    /// </summary>
    /// <remarks>
    /// Using [Collection(MultiFileTestFixture.Collection)] will allow this singleton to be injected via the test class constructor.
    /// https://xunit.net/docs/shared-context
    /// </remarks>
    [CollectionDefinition(Collection)]
    public sealed class MultiFileTestFixture : ICollectionFixture<MultiFileTestFixture>
    {
        public const string Collection = "Uses MSBuild";
        /// <summary>
        /// Turn it and run the test, then you can manually check the output loads/builds in VS.
        /// </summary>
        private readonly bool _writeAllFilesForManualTesting = false;

        private static readonly string MultiFileCharacterizationDir = Path.Combine(TestConstants.GetTestDataDirectory(), "MultiFileCharacterization");
        private static readonly string OriginalSolutionDir = Path.Combine(MultiFileCharacterizationDir, "SourceFiles");
        private static readonly string SolutionFile = Path.Combine(OriginalSolutionDir, "CharacterizationTestSolution.sln");
        private static readonly MSBuildWorkspaceConverter _msBuildWorkspaceConverter = new MSBuildWorkspaceConverter(SolutionFile, false);

        public async Task ConvertProjectsWhere(Func<Project, bool> shouldConvertProject, Language targetLanguage, [CallerMemberName] string expectedResultsDirectory = "")
        {
            bool recharacterizeByWritingExpectedOverActual = TestConstants.RecharacterizeByWritingExpectedOverActual;

            var results = await _msBuildWorkspaceConverter.ConvertProjectsWhereAsync(shouldConvertProject, targetLanguage, new Progress<ConversionProgress>(), default).ToArrayAsync();
            var conversionResults = results.ToDictionary(c => c.TargetPathOrNull, StringComparer.OrdinalIgnoreCase);
            var expectedResultDirectory = GetExpectedResultDirectory(expectedResultsDirectory, targetLanguage);

            try {
                if (!expectedResultDirectory.Exists) expectedResultDirectory.Create();
                var expectedFiles = expectedResultDirectory.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => !f.FullName.Contains(@"\obj\") && !f.FullName.Contains(@"\bin\")).ToArray();
                AssertAllExpectedFilesAreEqual(expectedFiles, conversionResults, expectedResultDirectory, OriginalSolutionDir);
                AssertAllConvertedFilesWereExpected(expectedFiles, conversionResults, expectedResultDirectory, OriginalSolutionDir);
                AssertNoConversionErrors(conversionResults);
            } finally {
                if (recharacterizeByWritingExpectedOverActual) {
                    var things = ConversionResultWriter.WriteConvertedAsync(results.ToAsyncEnumerable(), SolutionFile, expectedResultDirectory, true, _writeAllFilesForManualTesting, new Progress<string>(), default);
                }
            }

            Assert.False(recharacterizeByWritingExpectedOverActual, $"Test setup issue: Set {nameof(recharacterizeByWritingExpectedOverActual)} to false after using it");
        }

        private static void AssertAllConvertedFilesWereExpected(FileInfo[] expectedFiles,
            Dictionary<string, ConversionResult> conversionResults, DirectoryInfo expectedResultDirectory,
            string originalSolutionDir)
        {
            AssertSubset(expectedFiles.Select(f => f.FullName.Replace(expectedResultDirectory.FullName, "")), conversionResults.Select(r => r.Key.Replace(originalSolutionDir, "")),
                "Extra unexpected files were converted");
        }

        private void AssertAllExpectedFilesAreEqual(FileInfo[] expectedFiles, Dictionary<string, ConversionResult> conversionResults,
            DirectoryInfo expectedResultDirectory, string originalSolutionDir)
        {
            foreach (var expectedFile in expectedFiles) {
                AssertFileEqual(conversionResults, expectedResultDirectory, expectedFile, originalSolutionDir);
            }
        }

        private static void AssertNoConversionErrors(Dictionary<string, ConversionResult> conversionResults)
        {
            var errors = conversionResults
                .SelectMany(r => (r.Value.Exceptions ?? Array.Empty<string>()).Select(e => new { Path = r.Key, Exception = e }))
                .ToList();
            Assert.Empty(errors);
        }

        private static void AssertSubset(IEnumerable<string> superset, IEnumerable<string> subset, string userMessage)
        {
            var notExpected = new HashSet<string>(subset, StringComparer.OrdinalIgnoreCase);
            notExpected.ExceptWith(new HashSet<string>(superset, StringComparer.OrdinalIgnoreCase));
            Assert.False(notExpected.Any(), userMessage + "\r\n" + string.Join("\r\n", notExpected));
        }

        private void AssertFileEqual(Dictionary<string, ConversionResult> conversionResults,
            DirectoryInfo expectedResultDirectory,
            FileInfo expectedFile,
            string actualSolutionDir)
        {
            var convertedFilePath = expectedFile.FullName.Replace(expectedResultDirectory.FullName, actualSolutionDir);
            var fileDidNotNeedConversion = !conversionResults.ContainsKey(convertedFilePath) && File.Exists(convertedFilePath);
            if (fileDidNotNeedConversion) return;

            Assert.True(conversionResults.ContainsKey(convertedFilePath), expectedFile.Name + " is missing from the conversion result of [" + string.Join(",", conversionResults.Keys) + "]");

            var expectedText = File.ReadAllText(expectedFile.FullName);
            var conversionResult = conversionResults[convertedFilePath];
            var actualText = conversionResult.ConvertedCode ?? "" + conversionResult.GetExceptionsAsString() ?? "";

            OurAssert.EqualIgnoringNewlines(expectedText, actualText);
            Assert.Equal(GetEncoding(expectedFile.FullName), GetEncoding(conversionResult));
        }

        private Encoding GetEncoding(ConversionResult conversionResult)
        {
            var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            conversionResult.TargetPathOrNull = filePath;
            conversionResult.WriteToFile();
            var encoding = GetEncoding(filePath);
            File.Delete(filePath);
            return encoding;
        }

        private static Encoding GetEncoding(string filePath)
        {
            using (var reader = new StreamReader(filePath, true)) {
                reader.Peek();
                return reader.CurrentEncoding;
            }
        }

        private static DirectoryInfo GetExpectedResultDirectory(string testFolderName, Language targetLanguage)
        {
            string languagePrefix = targetLanguage == Language.CS ? "VBToCS" : "CSToVB";
            string conversionDirectionFolderName = languagePrefix + "Results";
            var path = Path.Combine(MultiFileCharacterizationDir, conversionDirectionFolderName, testFolderName);
            return new DirectoryInfo(path);
        }
    }
}