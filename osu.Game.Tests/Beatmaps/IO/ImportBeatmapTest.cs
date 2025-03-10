﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Game.IPC;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.IO;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Scoring;
using osu.Game.Tests.Resources;
using osu.Game.Tests.Scores.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Writers.Zip;
using FileInfo = System.IO.FileInfo;

namespace osu.Game.Tests.Beatmaps.IO
{
    [TestFixture]
    public class ImportBeatmapTest : ImportTest
    {
        [Test]
        public async Task TestImportWhenClosed()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    await LoadOszIntoOsu(LoadOsuIntoHost(host));
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenDelete()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var imported = await LoadOszIntoOsu(osu);

                    deleteBeatmapSet(imported, osu);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenDeleteFromStream()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string tempPath = TestResources.GetTestBeatmapForImport();

                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    ILive<BeatmapSetInfo> importedSet;

                    using (var stream = File.OpenRead(tempPath))
                    {
                        importedSet = await manager.Import(new ImportTask(stream, Path.GetFileName(tempPath)));
                        ensureLoaded(osu);
                    }

                    Assert.IsTrue(File.Exists(tempPath), "Stream source file somehow went missing");
                    File.Delete(tempPath);

                    var imported = manager.GetAllUsableBeatmapSets().Find(beatmapSet => beatmapSet.ID == importedSet.Value.ID);

                    deleteBeatmapSet(imported, osu);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenImport()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var imported = await LoadOszIntoOsu(osu);
                    var importedSecondTime = await LoadOszIntoOsu(osu);

                    // check the newly "imported" beatmap is actually just the restored previous import. since it matches hash.
                    Assert.IsTrue(imported.ID == importedSecondTime.ID);
                    Assert.IsTrue(imported.Beatmaps.First().ID == importedSecondTime.Beatmaps.First().ID);

                    checkBeatmapSetCount(osu, 1);
                    checkSingleReferencedFileCount(osu, 18);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenImportWithReZip()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    Directory.CreateDirectory(extractedFolder);

                    try
                    {
                        var imported = await LoadOszIntoOsu(osu);

                        string hashBefore = hashFile(temp);

                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(extractedFolder);

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        // zip files differ because different compression or encoder.
                        Assert.AreNotEqual(hashBefore, hashFile(temp));

                        var importedSecondTime = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        // but contents doesn't, so existing should still be used.
                        Assert.IsTrue(imported.ID == importedSecondTime.Value.ID);
                        Assert.IsTrue(imported.Beatmaps.First().ID == importedSecondTime.Value.Beatmaps.First().ID);
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenImportWithChangedHashedFile()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    Directory.CreateDirectory(extractedFolder);

                    try
                    {
                        var imported = await LoadOszIntoOsu(osu);

                        await createScoreForBeatmap(osu, imported.Beatmaps.First());

                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(extractedFolder);

                        // arbitrary write to hashed file
                        // this triggers the special BeatmapManager.PreImport deletion/replacement flow.
                        using (var sw = new FileInfo(Directory.GetFiles(extractedFolder, "*.osu").First()).AppendText())
                            await sw.WriteLineAsync("// changed");

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        var importedSecondTime = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        // check the newly "imported" beatmap is not the original.
                        Assert.IsTrue(imported.ID != importedSecondTime.Value.ID);
                        Assert.IsTrue(imported.Beatmaps.First().ID != importedSecondTime.Value.Beatmaps.First().ID);
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        [Ignore("intentionally broken by import optimisations")]
        public async Task TestImportThenImportWithChangedFile()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    Directory.CreateDirectory(extractedFolder);

                    try
                    {
                        var imported = await LoadOszIntoOsu(osu);

                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(extractedFolder);

                        // arbitrary write to non-hashed file
                        using (var sw = new FileInfo(Directory.GetFiles(extractedFolder, "*.mp3").First()).AppendText())
                            await sw.WriteLineAsync("text");

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        var importedSecondTime = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        // check the newly "imported" beatmap is not the original.
                        Assert.IsTrue(imported.ID != importedSecondTime.Value.ID);
                        Assert.IsTrue(imported.Beatmaps.First().ID != importedSecondTime.Value.Beatmaps.First().ID);
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenImportWithDifferentFilename()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    Directory.CreateDirectory(extractedFolder);

                    try
                    {
                        var imported = await LoadOszIntoOsu(osu);

                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(extractedFolder);

                        // change filename
                        var firstFile = new FileInfo(Directory.GetFiles(extractedFolder).First());
                        firstFile.MoveTo(Path.Combine(firstFile.DirectoryName.AsNonNull(), $"{firstFile.Name}-changed{firstFile.Extension}"));

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        var importedSecondTime = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        // check the newly "imported" beatmap is not the original.
                        Assert.IsTrue(imported.ID != importedSecondTime.Value.ID);
                        Assert.IsTrue(imported.Beatmaps.First().ID != importedSecondTime.Value.Beatmaps.First().ID);
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        [Ignore("intentionally broken by import optimisations")]
        public async Task TestImportCorruptThenImport()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var imported = await LoadOszIntoOsu(osu);

                    var firstFile = imported.Files.First();

                    var files = osu.Dependencies.Get<FileStore>();

                    long originalLength;
                    using (var stream = files.Storage.GetStream(firstFile.FileInfo.StoragePath))
                        originalLength = stream.Length;

                    using (var stream = files.Storage.GetStream(firstFile.FileInfo.StoragePath, FileAccess.Write, FileMode.Create))
                        stream.WriteByte(0);

                    var importedSecondTime = await LoadOszIntoOsu(osu);

                    using (var stream = files.Storage.GetStream(firstFile.FileInfo.StoragePath))
                        Assert.AreEqual(stream.Length, originalLength, "Corruption was not fixed on second import");

                    // check the newly "imported" beatmap is actually just the restored previous import. since it matches hash.
                    Assert.IsTrue(imported.ID == importedSecondTime.ID);
                    Assert.IsTrue(imported.Beatmaps.First().ID == importedSecondTime.Beatmaps.First().ID);

                    checkBeatmapSetCount(osu, 1);
                    checkSingleReferencedFileCount(osu, 18);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestModelCreationFailureDoesntReturn()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    var importer = osu.Dependencies.Get<BeatmapManager>();

                    var progressNotification = new ImportProgressNotification();

                    var zipStream = new MemoryStream();

                    using (var zip = ZipArchive.Create())
                        zip.SaveTo(zipStream, new ZipWriterOptions(CompressionType.Deflate));

                    var imported = await importer.Import(
                        progressNotification,
                        new ImportTask(zipStream, string.Empty)
                    );

                    checkBeatmapSetCount(osu, 0);
                    checkBeatmapCount(osu, 0);

                    Assert.IsEmpty(imported);
                    Assert.AreEqual(ProgressNotificationState.Cancelled, progressNotification.State);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestRollbackOnFailure()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    int itemAddRemoveFireCount = 0;
                    int loggedExceptionCount = 0;

                    Logger.NewEntry += l =>
                    {
                        if (l.Target == LoggingTarget.Database && l.Exception != null)
                            Interlocked.Increment(ref loggedExceptionCount);
                    };

                    var osu = LoadOsuIntoHost(host);
                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    // ReSharper disable once AccessToModifiedClosure
                    manager.ItemUpdated += _ => Interlocked.Increment(ref itemAddRemoveFireCount);
                    manager.ItemRemoved += _ => Interlocked.Increment(ref itemAddRemoveFireCount);

                    var imported = await LoadOszIntoOsu(osu);

                    Assert.AreEqual(0, itemAddRemoveFireCount -= 1);

                    imported.Hash += "-changed";
                    manager.Update(imported);

                    Assert.AreEqual(0, itemAddRemoveFireCount -= 1);

                    checkBeatmapSetCount(osu, 1);
                    checkBeatmapCount(osu, 12);
                    checkSingleReferencedFileCount(osu, 18);

                    string brokenTempFilename = TestResources.GetTestBeatmapForImport();

                    MemoryStream brokenOsu = new MemoryStream();
                    MemoryStream brokenOsz = new MemoryStream(await File.ReadAllBytesAsync(brokenTempFilename));

                    File.Delete(brokenTempFilename);

                    using (var outStream = File.Open(brokenTempFilename, FileMode.CreateNew))
                    using (var zip = ZipArchive.Open(brokenOsz))
                    {
                        zip.AddEntry("broken.osu", brokenOsu, false);
                        zip.SaveTo(outStream, CompressionType.Deflate);
                    }

                    // this will trigger purging of the existing beatmap (online set id match) but should rollback due to broken osu.
                    try
                    {
                        await manager.Import(new ImportTask(brokenTempFilename));
                    }
                    catch
                    {
                    }

                    // no events should be fired in the case of a rollback.
                    Assert.AreEqual(0, itemAddRemoveFireCount);

                    checkBeatmapSetCount(osu, 1);
                    checkBeatmapCount(osu, 12);

                    checkSingleReferencedFileCount(osu, 18);

                    Assert.AreEqual(1, loggedExceptionCount);

                    File.Delete(brokenTempFilename);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenDeleteThenImport()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var imported = await LoadOszIntoOsu(osu);

                    deleteBeatmapSet(imported, osu);

                    var importedSecondTime = await LoadOszIntoOsu(osu);

                    // check the newly "imported" beatmap is actually just the restored previous import. since it matches hash.
                    Assert.IsTrue(imported.ID == importedSecondTime.ID);
                    Assert.IsTrue(imported.Beatmaps.First().ID == importedSecondTime.Beatmaps.First().ID);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportThenDeleteThenImportWithOnlineIDsMissing()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost($"{nameof(ImportBeatmapTest)}"))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var imported = await LoadOszIntoOsu(osu);

                    foreach (var b in imported.Beatmaps)
                        b.OnlineID = null;

                    osu.Dependencies.Get<BeatmapManager>().Update(imported);

                    deleteBeatmapSet(imported, osu);

                    var importedSecondTime = await LoadOszIntoOsu(osu);

                    // check the newly "imported" beatmap has been reimported due to mismatch (even though hashes matched)
                    Assert.IsTrue(imported.ID != importedSecondTime.ID);
                    Assert.IsTrue(imported.Beatmaps.First().ID != importedSecondTime.Beatmaps.First().ID);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportWithDuplicateBeatmapIDs()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var metadata = new BeatmapMetadata
                    {
                        Artist = "SomeArtist",
                        AuthorString = "SomeAuthor"
                    };

                    var difficulty = new BeatmapDifficulty();

                    var toImport = new BeatmapSetInfo
                    {
                        OnlineID = 1,
                        Metadata = metadata,
                        Beatmaps = new List<BeatmapInfo>
                        {
                            new BeatmapInfo
                            {
                                OnlineID = 2,
                                Metadata = metadata,
                                BaseDifficulty = difficulty
                            },
                            new BeatmapInfo
                            {
                                OnlineID = 2,
                                Metadata = metadata,
                                Status = BeatmapSetOnlineStatus.Loved,
                                BaseDifficulty = difficulty
                            }
                        }
                    };

                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    var imported = await manager.Import(toImport);

                    Assert.NotNull(imported);
                    Assert.AreEqual(null, imported.Value.Beatmaps[0].OnlineID);
                    Assert.AreEqual(null, imported.Value.Beatmaps[1].OnlineID);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        [NonParallelizable]
        public void TestImportOverIPC()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost($"{nameof(ImportBeatmapTest)}-host", true))
            using (HeadlessGameHost client = new CleanRunHeadlessGameHost($"{nameof(ImportBeatmapTest)}-client", true))
            {
                try
                {
                    Assert.IsTrue(host.IsPrimaryInstance);
                    Assert.IsFalse(client.IsPrimaryInstance);

                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    var importer = new ArchiveImportIPCChannel(client);
                    if (!importer.ImportAsync(temp).Wait(10000))
                        Assert.Fail(@"IPC took too long to send");

                    ensureLoaded(osu);

                    waitForOrAssert(() => !File.Exists(temp), "Temporary still exists after IPC import", 5000);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportWhenFileOpen()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    string temp = TestResources.GetTestBeatmapForImport();
                    using (File.OpenRead(temp))
                        await osu.Dependencies.Get<BeatmapManager>().Import(temp);
                    ensureLoaded(osu);
                    File.Delete(temp);
                    Assert.IsFalse(File.Exists(temp), "We likely held a read lock on the file when we shouldn't");
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportWithDuplicateHashes()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    Directory.CreateDirectory(extractedFolder);

                    try
                    {
                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(extractedFolder);

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.AddEntry("duplicate.osu", Directory.GetFiles(extractedFolder, "*.osu").First());
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        await osu.Dependencies.Get<BeatmapManager>().Import(temp);

                        ensureLoaded(osu);
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportNestedStructure()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    string subfolder = Path.Combine(extractedFolder, "subfolder");

                    Directory.CreateDirectory(subfolder);

                    try
                    {
                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(subfolder);

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        var imported = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        Assert.IsFalse(imported.Value.Files.Any(f => f.Filename.Contains("subfolder")), "Files contain common subfolder");
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestImportWithIgnoredDirectoryInArchive()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    string temp = TestResources.GetTestBeatmapForImport();

                    string extractedFolder = $"{temp}_extracted";
                    string dataFolder = Path.Combine(extractedFolder, "actual_data");
                    string resourceForkFolder = Path.Combine(extractedFolder, "__MACOSX");
                    string resourceForkFilePath = Path.Combine(resourceForkFolder, ".extracted");

                    Directory.CreateDirectory(dataFolder);
                    Directory.CreateDirectory(resourceForkFolder);

                    using (var resourceForkFile = File.CreateText(resourceForkFilePath))
                    {
                        await resourceForkFile.WriteLineAsync("adding content so that it's not empty");
                    }

                    try
                    {
                        using (var zip = ZipArchive.Open(temp))
                            zip.WriteToDirectory(dataFolder);

                        using (var zip = ZipArchive.Create())
                        {
                            zip.AddAllFromDirectory(extractedFolder);
                            zip.SaveTo(temp, new ZipWriterOptions(CompressionType.Deflate));
                        }

                        var imported = await osu.Dependencies.Get<BeatmapManager>().Import(new ImportTask(temp));

                        ensureLoaded(osu);

                        Assert.IsFalse(imported.Value.Files.Any(f => f.Filename.Contains("__MACOSX")), "Files contain resource fork folder, which should be ignored");
                        Assert.IsFalse(imported.Value.Files.Any(f => f.Filename.Contains("actual_data")), "Files contain common subfolder");
                    }
                    finally
                    {
                        Directory.Delete(extractedFolder, true);
                    }
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestUpdateBeatmapInfo()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    string temp = TestResources.GetTestBeatmapForImport();
                    await osu.Dependencies.Get<BeatmapManager>().Import(temp);

                    // Update via the beatmap, not the beatmap info, to ensure correct linking
                    BeatmapSetInfo setToUpdate = manager.GetAllUsableBeatmapSets()[0];
                    Beatmap beatmapToUpdate = (Beatmap)manager.GetWorkingBeatmap(setToUpdate.Beatmaps.First(b => b.RulesetID == 0)).Beatmap;
                    beatmapToUpdate.BeatmapInfo.DifficultyName = "updated";

                    manager.Update(setToUpdate);

                    BeatmapInfo updatedInfo = manager.QueryBeatmap(b => b.ID == beatmapToUpdate.BeatmapInfo.ID);
                    Assert.That(updatedInfo.DifficultyName, Is.EqualTo("updated"));
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public async Task TestUpdateBeatmapFile()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    string temp = TestResources.GetTestBeatmapForImport();
                    await osu.Dependencies.Get<BeatmapManager>().Import(temp);

                    BeatmapSetInfo setToUpdate = manager.GetAllUsableBeatmapSets()[0];

                    var beatmapInfo = setToUpdate.Beatmaps.First(b => b.RulesetID == 0);
                    Beatmap beatmapToUpdate = (Beatmap)manager.GetWorkingBeatmap(setToUpdate.Beatmaps.First(b => b.RulesetID == 0)).Beatmap;
                    BeatmapSetFileInfo fileToUpdate = setToUpdate.Files.First(f => beatmapToUpdate.BeatmapInfo.Path.Contains(f.Filename));

                    string oldMd5Hash = beatmapToUpdate.BeatmapInfo.MD5Hash;

                    beatmapToUpdate.HitObjects.Clear();
                    beatmapToUpdate.HitObjects.Add(new HitCircle { StartTime = 5000 });

                    manager.Save(beatmapInfo, beatmapToUpdate);

                    // Check that the old file reference has been removed
                    Assert.That(manager.QueryBeatmapSet(s => s.ID == setToUpdate.ID).Files.All(f => f.ID != fileToUpdate.ID));

                    // Check that the new file is referenced correctly by attempting a retrieval
                    Beatmap updatedBeatmap = (Beatmap)manager.GetWorkingBeatmap(manager.QueryBeatmap(b => b.ID == beatmapToUpdate.BeatmapInfo.ID)).Beatmap;
                    Assert.That(updatedBeatmap.HitObjects.Count, Is.EqualTo(1));
                    Assert.That(updatedBeatmap.HitObjects[0].StartTime, Is.EqualTo(5000));
                    Assert.That(updatedBeatmap.BeatmapInfo.MD5Hash, Is.Not.EqualTo(oldMd5Hash));
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        // TODO: needs to be pulled across to realm implementation when this file is nuked.
        [Test]
        public void TestSaveRemovesInvalidCharactersFromPath()
        {
            // unfortunately for the time being we need to reference osu.Framework.Desktop for a game host here.
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);

                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    var working = manager.CreateNew(new OsuRuleset().RulesetInfo, APIUser.SYSTEM_USER);

                    var beatmap = working.Beatmap;

                    beatmap.BeatmapInfo.DifficultyName = "difficulty";
                    beatmap.BeatmapInfo.Metadata = new BeatmapMetadata
                    {
                        Artist = "Artist/With\\Slashes",
                        Title = "Title",
                        AuthorString = "mapper",
                    };

                    manager.Save(beatmap.BeatmapInfo, working.Beatmap);

                    Assert.AreEqual("Artist_With_Slashes - Title (mapper) [difficulty].osu", beatmap.BeatmapInfo.Path);
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public void TestCreateNewEmptyBeatmap()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    var working = manager.CreateNew(new OsuRuleset().RulesetInfo, APIUser.SYSTEM_USER);

                    manager.Save(working.BeatmapInfo, working.Beatmap);

                    var retrievedSet = manager.GetAllUsableBeatmapSets()[0];

                    // Check that the new file is referenced correctly by attempting a retrieval
                    Beatmap updatedBeatmap = (Beatmap)manager.GetWorkingBeatmap(retrievedSet.Beatmaps[0]).Beatmap;
                    Assert.That(updatedBeatmap.HitObjects.Count, Is.EqualTo(0));
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        [Test]
        public void TestCreateNewBeatmapWithObject()
        {
            using (HeadlessGameHost host = new CleanRunHeadlessGameHost(nameof(ImportBeatmapTest)))
            {
                try
                {
                    var osu = LoadOsuIntoHost(host);
                    var manager = osu.Dependencies.Get<BeatmapManager>();

                    var working = manager.CreateNew(new OsuRuleset().RulesetInfo, APIUser.SYSTEM_USER);

                    ((Beatmap)working.Beatmap).HitObjects.Add(new HitCircle { StartTime = 5000 });

                    manager.Save(working.BeatmapInfo, working.Beatmap);

                    var retrievedSet = manager.GetAllUsableBeatmapSets()[0];

                    // Check that the new file is referenced correctly by attempting a retrieval
                    Beatmap updatedBeatmap = (Beatmap)manager.GetWorkingBeatmap(retrievedSet.Beatmaps[0]).Beatmap;
                    Assert.That(updatedBeatmap.HitObjects.Count, Is.EqualTo(1));
                    Assert.That(updatedBeatmap.HitObjects[0].StartTime, Is.EqualTo(5000));
                }
                finally
                {
                    host.Exit();
                }
            }
        }

        public static async Task<BeatmapSetInfo> LoadQuickOszIntoOsu(OsuGameBase osu)
        {
            string temp = TestResources.GetQuickTestBeatmapForImport();

            var manager = osu.Dependencies.Get<BeatmapManager>();

            var importedSet = await manager.Import(new ImportTask(temp)).ConfigureAwait(false);

            ensureLoaded(osu);

            waitForOrAssert(() => !File.Exists(temp), "Temporary file still exists after standard import", 5000);

            return manager.GetAllUsableBeatmapSets().Find(beatmapSet => beatmapSet.ID == importedSet.Value.ID);
        }

        public static async Task<BeatmapSetInfo> LoadOszIntoOsu(OsuGameBase osu, string path = null, bool virtualTrack = false)
        {
            string temp = path ?? TestResources.GetTestBeatmapForImport(virtualTrack);

            var manager = osu.Dependencies.Get<BeatmapManager>();

            var importedSet = await manager.Import(new ImportTask(temp)).ConfigureAwait(false);

            ensureLoaded(osu);

            waitForOrAssert(() => !File.Exists(temp), "Temporary file still exists after standard import", 5000);

            return manager.GetAllUsableBeatmapSets().Find(beatmapSet => beatmapSet.ID == importedSet.Value.ID);
        }

        private void deleteBeatmapSet(BeatmapSetInfo imported, OsuGameBase osu)
        {
            var manager = osu.Dependencies.Get<BeatmapManager>();
            manager.Delete(imported);

            checkBeatmapSetCount(osu, 0);
            checkBeatmapSetCount(osu, 1, true);
            checkSingleReferencedFileCount(osu, 0);

            Assert.IsTrue(manager.QueryBeatmapSets(_ => true).First().DeletePending);
        }

        private static Task createScoreForBeatmap(OsuGameBase osu, BeatmapInfo beatmapInfo)
        {
            return ImportScoreTest.LoadScoreIntoOsu(osu, new ScoreInfo
            {
                OnlineScoreID = 2,
                BeatmapInfo = beatmapInfo,
                BeatmapInfoID = beatmapInfo.ID
            }, new ImportScoreTest.TestArchiveReader());
        }

        private static void checkBeatmapSetCount(OsuGameBase osu, int expected, bool includeDeletePending = false)
        {
            var manager = osu.Dependencies.Get<BeatmapManager>();

            Assert.AreEqual(expected, includeDeletePending
                ? manager.QueryBeatmapSets(_ => true).ToList().Count
                : manager.GetAllUsableBeatmapSets().Count);
        }

        private static string hashFile(string filename)
        {
            using (var s = File.OpenRead(filename))
                return s.ComputeMD5Hash();
        }

        private static void checkBeatmapCount(OsuGameBase osu, int expected)
        {
            Assert.AreEqual(expected, osu.Dependencies.Get<BeatmapManager>().QueryBeatmaps(_ => true).ToList().Count);
        }

        private static void checkSingleReferencedFileCount(OsuGameBase osu, int expected)
        {
            Assert.AreEqual(expected, osu.Dependencies.Get<FileStore>().QueryFiles(f => f.ReferenceCount == 1).Count());
        }

        private static void ensureLoaded(OsuGameBase osu, int timeout = 60000)
        {
            IEnumerable<BeatmapSetInfo> resultSets = null;
            var store = osu.Dependencies.Get<BeatmapManager>();
            waitForOrAssert(() => (resultSets = store.QueryBeatmapSets(s => s.OnlineID == 241526)).Any(),
                @"BeatmapSet did not import to the database in allocated time.", timeout);

            // ensure we were stored to beatmap database backing...
            Assert.IsTrue(resultSets.Count() == 1, $@"Incorrect result count found ({resultSets.Count()} but should be 1).");
            IEnumerable<BeatmapInfo> queryBeatmaps() => store.QueryBeatmaps(s => s.BeatmapSet.OnlineID == 241526 && s.BaseDifficultyID > 0);
            IEnumerable<BeatmapSetInfo> queryBeatmapSets() => store.QueryBeatmapSets(s => s.OnlineID == 241526);

            // if we don't re-check here, the set will be inserted but the beatmaps won't be present yet.
            waitForOrAssert(() => queryBeatmaps().Count() == 12,
                @"Beatmaps did not import to the database in allocated time", timeout);
            waitForOrAssert(() => queryBeatmapSets().Count() == 1,
                @"BeatmapSet did not import to the database in allocated time", timeout);
            int countBeatmapSetBeatmaps = 0;
            int countBeatmaps = 0;
            waitForOrAssert(() =>
                    (countBeatmapSetBeatmaps = queryBeatmapSets().First().Beatmaps.Count) ==
                    (countBeatmaps = queryBeatmaps().Count()),
                $@"Incorrect database beatmap count post-import ({countBeatmaps} but should be {countBeatmapSetBeatmaps}).", timeout);

            var set = queryBeatmapSets().First();
            foreach (BeatmapInfo b in set.Beatmaps)
                Assert.IsTrue(set.Beatmaps.Any(c => c.OnlineID == b.OnlineID));
            Assert.IsTrue(set.Beatmaps.Count > 0);
            var beatmap = store.GetWorkingBeatmap(set.Beatmaps.First(b => b.RulesetID == 0))?.Beatmap;
            Assert.IsTrue(beatmap?.HitObjects.Any() == true);
            beatmap = store.GetWorkingBeatmap(set.Beatmaps.First(b => b.RulesetID == 1))?.Beatmap;
            Assert.IsTrue(beatmap?.HitObjects.Any() == true);
            beatmap = store.GetWorkingBeatmap(set.Beatmaps.First(b => b.RulesetID == 2))?.Beatmap;
            Assert.IsTrue(beatmap?.HitObjects.Any() == true);
            beatmap = store.GetWorkingBeatmap(set.Beatmaps.First(b => b.RulesetID == 3))?.Beatmap;
            Assert.IsTrue(beatmap?.HitObjects.Any() == true);
        }

        private static void waitForOrAssert(Func<bool> result, string failureMessage, int timeout = 60000)
        {
            Task task = Task.Run(() =>
            {
                while (!result()) Thread.Sleep(200);
            });

            Assert.IsTrue(task.Wait(timeout), failureMessage);
        }
    }
}
