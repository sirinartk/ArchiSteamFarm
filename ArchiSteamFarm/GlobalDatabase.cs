//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// |
// Copyright 2015-2019 Łukasz "JustArchi" Domeradzki
// Contact: JustArchi@JustArchi.net
// |
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// |
// http://www.apache.org/licenses/LICENSE-2.0
// |
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.SteamKit2;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	public sealed class GlobalDatabase : IDisposable {
		[JsonProperty(Required = Required.DisallowNull)]
		public readonly Guid Guid = Guid.NewGuid();

		[JsonProperty(Required = Required.DisallowNull)]
		internal readonly ConcurrentDictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)> PackagesData = new ConcurrentDictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)>();

		[JsonProperty(Required = Required.DisallowNull)]
		internal readonly InMemoryServerListProvider ServerListProvider = new InMemoryServerListProvider();

		private readonly SemaphoreSlim FileSemaphore = new SemaphoreSlim(1, 1);
		private readonly SemaphoreSlim PackagesRefreshSemaphore = new SemaphoreSlim(1, 1);

		[JsonProperty(PropertyName = "_CellID", Required = Required.DisallowNull)]
		internal uint CellID { get; private set; }

		private string FilePath;

		private GlobalDatabase([NotNull] string filePath) : this() {
			if (string.IsNullOrEmpty(filePath)) {
				throw new ArgumentNullException(nameof(filePath));
			}

			FilePath = filePath;
		}

		[JsonConstructor]
		private GlobalDatabase() => ServerListProvider.ServerListUpdated += OnServerListUpdated;

		public void Dispose() {
			// Events we registered
			ServerListProvider.ServerListUpdated -= OnServerListUpdated;

			// Those are objects that are always being created if constructor doesn't throw exception
			FileSemaphore.Dispose();
			PackagesRefreshSemaphore.Dispose();
		}

		[ItemCanBeNull]
		internal static async Task<GlobalDatabase> CreateOrLoad(string filePath) {
			if (string.IsNullOrEmpty(filePath)) {
				ASF.ArchiLogger.LogNullError(nameof(filePath));

				return null;
			}

			if (!File.Exists(filePath)) {
				return new GlobalDatabase(filePath);
			}

			GlobalDatabase globalDatabase;

			try {
				string json = await RuntimeCompatibility.File.ReadAllTextAsync(filePath).ConfigureAwait(false);

				if (string.IsNullOrEmpty(json)) {
					ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsEmpty, nameof(json)));

					return null;
				}

				globalDatabase = JsonConvert.DeserializeObject<GlobalDatabase>(json);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				return null;
			}

			if (globalDatabase == null) {
				ASF.ArchiLogger.LogNullError(nameof(globalDatabase));

				return null;
			}

			globalDatabase.FilePath = filePath;

			return globalDatabase;
		}

		internal HashSet<uint> GetPackageIDs(uint appID, ICollection<uint> packageIDs) {
			if ((appID == 0) || (packageIDs == null) || (packageIDs.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(packageIDs));

				return null;
			}

			HashSet<uint> result = new HashSet<uint>();

			foreach (uint packageID in packageIDs.Where(packageID => packageID != 0)) {
				if (!PackagesData.TryGetValue(packageID, out (uint _, HashSet<uint> AppIDs) packagesData) || (packagesData.AppIDs?.Contains(appID) != true)) {
					continue;
				}

				result.Add(packageID);
			}

			return result;
		}

		internal async Task RefreshPackages(Bot bot, IReadOnlyDictionary<uint, uint> packages) {
			if ((bot == null) || (packages == null) || (packages.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(packages));

				return;
			}

			await PackagesRefreshSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				HashSet<uint> packageIDs = packages.Where(package => (package.Key != 0) && (!PackagesData.TryGetValue(package.Key, out (uint ChangeNumber, HashSet<uint> _) packageData) || (packageData.ChangeNumber < package.Value))).Select(package => package.Key).ToHashSet();

				if (packageIDs.Count == 0) {
					return;
				}

				Dictionary<uint, (uint ChangeNumber, HashSet<uint> AppIDs)> packagesData = await bot.GetPackagesData(packageIDs).ConfigureAwait(false);

				if ((packagesData == null) || (packagesData.Count == 0)) {
					bot.ArchiLogger.LogGenericWarning(Strings.WarningFailed);

					return;
				}

				foreach ((uint packageID, (uint ChangeNumber, HashSet<uint> AppIDs) package) in packagesData) {
					PackagesData[packageID] = package;
				}

				await Save().ConfigureAwait(false);
			} finally {
				PackagesRefreshSemaphore.Release();
			}
		}

		internal async Task SetCellID(uint value = 0) {
			if (value == CellID) {
				return;
			}

			CellID = value;
			await Save().ConfigureAwait(false);
		}

		private async void OnServerListUpdated(object sender, EventArgs e) => await Save().ConfigureAwait(false);

		private async Task Save() {
			string json = JsonConvert.SerializeObject(this);

			if (string.IsNullOrEmpty(json)) {
				ASF.ArchiLogger.LogNullError(nameof(json));

				return;
			}

			string newFilePath = FilePath + ".new";

			await FileSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				// We always want to write entire content to temporary file first, in order to never load corrupted data, also when target file doesn't exist
				await RuntimeCompatibility.File.WriteAllTextAsync(newFilePath, json).ConfigureAwait(false);

				if (File.Exists(FilePath)) {
					File.Replace(newFilePath, FilePath, null);
				} else {
					File.Move(newFilePath, FilePath);
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			} finally {
				FileSemaphore.Release();
			}
		}

		// ReSharper disable UnusedMember.Global
		public bool ShouldSerializeCellID() => CellID != 0;
		public bool ShouldSerializePackagesData() => PackagesData.Count > 0;
		public bool ShouldSerializeServerListProvider() => ServerListProvider.ShouldSerializeServerRecords();

		// ReSharper restore UnusedMember.Global
	}
}
