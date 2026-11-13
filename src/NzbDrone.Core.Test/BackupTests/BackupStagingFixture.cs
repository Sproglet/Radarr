using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.BackupTests
{
    public class BackupStagingFixture : CoreTest<BackupService>
    {
        private readonly string _temp = Path.GetFullPath("synthetic-backup-temp");
        private DatabaseType _type;

        [SetUp]
        public void Setup()
        {
            _type = DatabaseType.SQLite;
            Mocker.GetMock<IMainDatabase>().SetupGet(d => d.DatabaseType).Returns(() => _type);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(a => a.TempFolder).Returns(_temp);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(a => a.AppDataFolder).Returns(Path.GetFullPath("synthetic-backup-config"));
            Mocker.GetMock<IConfigService>().SetupGet(c => c.BackupFolder).Returns(Path.GetFullPath("synthetic-backup-destination"));
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderWritable(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFiles(It.IsAny<string>(), false)).Returns<string, bool>((folder, _) =>
                (_type == DatabaseType.SQLite ? new[] { "config.xml", "INFO", "radarr.db" } : new[] { "config.xml", "INFO" })
                    .Select(name => Path.Combine(folder, name)).ToArray());
        }

        private void VerifyStage(string path, string purpose)
        {
            Path.GetDirectoryName(path).Should().Be(_temp);
            var prefix = "radarr_" + purpose + "_";
            Path.GetFileName(path).Should().StartWith(prefix);
            Guid.TryParseExact(Path.GetFileName(path).Substring(prefix.Length), "N", out _).Should().BeTrue();
            Mocker.GetMock<IDiskProvider>().Verify(d => d.DeleteFolder(path, true), Times.Once());
            if (OsInfo.IsNotWindows)
            {
                Mocker.GetMock<IDiskProvider>().Verify(d => d.SetPermissions(path, "700", null), Times.Once());
            }
        }

        [TestCase(DatabaseType.SQLite)]
        [TestCase(DatabaseType.PostgreSQL)]
        public void each_backup_uses_its_own_private_stage_and_preserves_legacy_paths(DatabaseType type)
        {
            _type = type;
            var stages = new List<string>();
            Mocker.GetMock<IDiskTransferService>()
                .Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Callback<string, string, TransferMode, bool>((_, target, _, _) => stages.Add(Path.GetDirectoryName(target)));

            Subject.Backup(BackupType.Manual);
            Subject.Backup(BackupType.Manual);

            stages.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            foreach (var stage in stages) VerifyStage(stage, "backup");
            Mocker.GetMock<IDiskProvider>().Verify(d => d.EmptyFolder(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.FolderExists(Path.Combine(_temp, "radarr_backup")), Times.Never());
            Mocker.GetMock<IMakeDatabaseBackup>().Verify(d => d.BackupDatabase(It.IsAny<IDatabase>(), It.IsAny<string>()),
                type == DatabaseType.SQLite ? Times.Exactly(2) : Times.Never());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void concurrent_backup_calls_and_service_instances_never_share_staging(bool separateInstances)
        {
            var stages = new ConcurrentBag<string>();
            using var barrier = new Barrier(2);
            Mocker.GetMock<IDiskTransferService>()
                .Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false))
                .Callback<string, string, TransferMode, bool>((_, target, _, _) =>
                {
                    stages.Add(Path.GetDirectoryName(target));
                    barrier.SignalAndWait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                });
            var first = Subject;
            var second = separateInstances ? new BackupService(Mocker.GetMock<IMainDatabase>().Object, Mocker.GetMock<IMakeDatabaseBackup>().Object,
                Mocker.GetMock<IDiskTransferService>().Object, Mocker.GetMock<IDiskProvider>().Object, Mocker.GetMock<IAppFolderInfo>().Object,
                Mocker.GetMock<IArchiveService>().Object, Mocker.GetMock<IConfigService>().Object, TestLogger) : first;

            // Different destination types avoid claiming same-second archive-name coordination.
            Task.WaitAll(Task.Run(() => first.Backup(BackupType.Manual)), Task.Run(() => second.Backup(BackupType.Update)));

            stages.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            foreach (var stage in stages) VerifyStage(stage, "backup");
        }

        [TestCase("copy")]
        [TestCase("database")]
        [TestCase("archive")]
        public void backup_failure_cleans_only_its_own_stage(string failure)
        {
            string stage = null;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.EnsureFolder(It.Is<string>(p => p.StartsWith(_temp))))
                .Callback<string>(path => stage = path);
            if (failure == "copy") Mocker.GetMock<IDiskTransferService>().Setup(d => d.TransferFile(It.IsAny<string>(), It.IsAny<string>(), TransferMode.Copy, false)).Throws(new IOException("Synthetic copy failure"));
            else if (failure == "database") Mocker.GetMock<IMakeDatabaseBackup>().Setup(d => d.BackupDatabase(It.IsAny<IDatabase>(), It.IsAny<string>())).Throws(new IOException("Synthetic database failure"));
            else Mocker.GetMock<IArchiveService>().Setup(a => a.CreateZip(It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Throws(new IOException("Synthetic archive failure"));

            Action backup = () => Subject.Backup(BackupType.Manual);

            backup.Should().Throw<IOException>();
            VerifyStage(stage, "backup");
            Mocker.GetMock<IDiskProvider>().Verify(d => d.DeleteFolder(It.IsAny<string>(), true), Times.Once());
        }

        [Test]
        public void repeated_zip_restores_use_distinct_private_stages_and_keep_legacy_paths()
        {
            var stages = new List<string>();
            Mocker.GetMock<IArchiveService>().Setup(a => a.Extract(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((_, target) => stages.Add(target));

            Subject.Restore("SyntheticOne.zip");
            Subject.Restore("SyntheticTwo.zip");

            stages.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            foreach (var stage in stages) VerifyStage(stage, "backup_restore");
            Mocker.GetMock<IDiskProvider>().Verify(d => d.FolderExists(Path.Combine(_temp, "radarr_backup_restore")), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFile(It.IsAny<string>(), Mocker.GetMock<IAppFolderInfo>().Object.GetDatabaseRestore(), true), Times.Exactly(2));
        }

        [TestCase("extract")]
        [TestCase("move")]
        [TestCase("empty")]
        public void failed_zip_restore_removes_only_its_own_stage(string failure)
        {
            string stage = null;
            Mocker.GetMock<IDiskProvider>().Setup(d => d.EnsureFolder(It.Is<string>(p => p.StartsWith(_temp)))).Callback<string>(path => stage = path);
            if (failure == "extract") Mocker.GetMock<IArchiveService>().Setup(a => a.Extract(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("Synthetic extract failure"));
            else if (failure == "move") Mocker.GetMock<IDiskProvider>().Setup(d => d.MoveFile(It.IsAny<string>(), It.IsAny<string>(), true)).Throws(new IOException("Synthetic move failure"));
            else Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFiles(It.IsAny<string>(), false)).Returns(Array.Empty<string>());

            Action restore = () => Subject.Restore("SyntheticFailure.zip");

            if (failure == "empty") restore.Should().Throw<RestoreBackupFailedException>();
            else restore.Should().Throw<IOException>();
            VerifyStage(stage, "backup_restore");
            Mocker.GetMock<IDiskProvider>().Verify(d => d.DeleteFolder(It.IsAny<string>(), true), Times.Once());
        }

        [Test]
        public void raw_database_restore_keeps_its_existing_direct_move_behavior()
        {
            Subject.Restore("SyntheticDatabase.db");
            Mocker.GetMock<IDiskProvider>().Verify(d => d.MoveFile("SyntheticDatabase.db", Mocker.GetMock<IAppFolderInfo>().Object.GetDatabaseRestore(), true), Times.Once());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.EnsureFolder(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IArchiveService>().VerifyNoOtherCalls();
        }
    }
}
