namespace StockSharp.Algo.Storages
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;
	using Ecng.Serialization;

	using StockSharp.Messages;
	using StockSharp.Localization;

	interface IMarketDataStorageInfo
	{
		DateTimeOffset GetTime(object data);
	}

	interface IMarketDataStorageInfo<in TData> : IMarketDataStorageInfo
	{
		DateTimeOffset GetTime(TData data);
	}

	abstract class MarketDataStorage<TMessage, TId> : IMarketDataStorage<TMessage>, IMarketDataStorageInfo<TMessage>
		where TMessage : Message
	{
		private readonly Func<TMessage, DateTimeOffset> _getTime;
		private readonly Func<TMessage, SecurityId> _getSecurityId;
		private readonly Func<TMessage, TId> _getId;
		private readonly SynchronizedDictionary<DateTime, SyncObject> _syncRoots = new SynchronizedDictionary<DateTime, SyncObject>();
		private readonly SynchronizedDictionary<DateTime, IMarketDataMetaInfo> _dateMetaInfos = new SynchronizedDictionary<DateTime, IMarketDataMetaInfo>();

		protected MarketDataStorage(SecurityId securityId, object arg, Func<TMessage, DateTimeOffset> getTime, Func<TMessage, SecurityId> getSecurityId, Func<TMessage, TId> getId, IMarketDataSerializer<TMessage> serializer, IMarketDataStorageDrive drive)
		{
			if (securityId.IsDefault())
				throw new ArgumentException(LocalizedStrings.Str1025, nameof(securityId));

			SecurityId = securityId;

			AppendOnlyNew = true;

			_getTime = getTime ?? throw new ArgumentNullException(nameof(getTime));
			_getSecurityId = getSecurityId ?? throw new ArgumentNullException(nameof(getSecurityId));
			_getId = getId ?? throw new ArgumentNullException(nameof(getId));
			Drive = drive ?? throw new ArgumentNullException(nameof(drive));
			Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
			_arg = arg;
		}

		IEnumerable<DateTime> IMarketDataStorage.Dates => Drive.Dates;

		Type IMarketDataStorage.DataType => typeof(TMessage);

		public SecurityId SecurityId { get; }

		private readonly object _arg;
		object IMarketDataStorage.Arg => _arg;

		public bool AppendOnlyNew { get; set; }

		IMarketDataSerializer IMarketDataStorage.Serializer => Serializer;
		public IMarketDataSerializer<TMessage> Serializer { get; }

		public IMarketDataStorageDrive Drive { get; }

		protected DateTime GetTruncatedTime(TMessage data)
		{
			return _getTime(data).StorageTruncate(Serializer.TimePrecision).UtcDateTime;
		}

		private SyncObject GetSync(DateTime time)
		{
			return _syncRoots.SafeAdd(time);
		}

		private Stream LoadStream(DateTime date)
		{
			return Drive.LoadStream(date);
		}

		private bool SecurityIdEqual(SecurityId securityId)
		{
			return securityId.SecurityCode.CompareIgnoreCase(SecurityId.SecurityCode) && securityId.BoardCode.CompareIgnoreCase(SecurityId.BoardCode);
		}

		public int Save(IEnumerable<TMessage> data)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			var count = 0;

			foreach (var group in data.GroupBy(d =>
			{
				var securityId = _getSecurityId(d);

				if (!securityId.IsDefault() && !SecurityIdEqual(securityId))
					throw new ArgumentException(LocalizedStrings.Str1026Params.Put(typeof(TMessage).Name, securityId, SecurityId));

				var time = _getTime(d);

				if (time == DateTimeOffset.MinValue)
					throw new ArgumentException(LocalizedStrings.EmptyMessageTime.Put(d));

				return time.UtcDateTime.Date;
			}))
			{
				var date = group.Key;
				var newItems = group.OrderBy(_getTime).ToArray();

				lock (GetSync(date))
				{
					var stream = LoadStream(date);

					try
					{
						var metaInfo = GetInfo(stream, date);

						if (metaInfo == null)
						{
							stream = new MemoryStream();
							metaInfo = Serializer.CreateMetaInfo(date);
						}

						var diff = Save(stream, metaInfo, newItems, false);

						if (diff == 0)
							continue;

						count += diff;

						if (!(stream is MemoryStream))
							continue;

						stream.Position = 0;
						Drive.SaveStream(date, stream);
					}
					finally
					{
						stream.Dispose();
					}
				}
			}

			return count;
		}

		private int Save(Stream stream, IMarketDataMetaInfo metaInfo, TMessage[] data, bool isOverride)
		{
			if (stream == null)
				throw new ArgumentNullException(nameof(stream));

			if (metaInfo == null)
				throw new ArgumentNullException(nameof(metaInfo));

			if (data == null)
				throw new ArgumentNullException(nameof(data));

			if (data.Length == 0)
				throw new ArgumentOutOfRangeException(nameof(data));

			if (metaInfo.Count == 0)
			{
				data = FilterNewData(data, metaInfo).ToArray();

				if (data.IsEmpty())
					return 0;

				var time = GetTruncatedTime(data[0]);

				//var priceStep = Security.PriceStep;
				//var volumeStep = Security.VolumeStep;

				//metaInfo.PriceStep = priceStep == null || priceStep == 0 ? 0.01m : priceStep.Value;
				//metaInfo.VolumeStep = volumeStep == null || volumeStep == 0 ? 1m : volumeStep.Value;
				metaInfo.LastTime = time;
				metaInfo.FirstTime = time;

				/*metaInfo.FirstPriceStep = */((MetaInfo)metaInfo).LastPriceStep = metaInfo.PriceStep;
			}
			else
			{
				if (AppendOnlyNew)
				{
					data = FilterNewData(data, metaInfo).ToArray();

					if (data.IsEmpty())
						return 0;
				}
			}

			var newDayData = new MemoryStream();

			Serializer.Serialize(newDayData, data, metaInfo);

			if (isOverride)
				metaInfo.Count = data.Length;
			else
				metaInfo.Count += data.Length;

			stream.Position = 0;
			metaInfo.Write(stream);

			if (isOverride || metaInfo.IsOverride)
				stream.SetLength(stream.Position);
			else
				stream.Position = stream.Length;

			newDayData.Position = 0;
			stream.WriteRaw(newDayData.To<byte[]>());

			return data.Length;
		}

		protected virtual IEnumerable<TMessage> FilterNewData(IEnumerable<TMessage> data, IMarketDataMetaInfo metaInfo)
		{
			var lastTime = metaInfo.LastTime;

			foreach (var item in data)
			{
				var time = GetTruncatedTime(item);

				if (time < lastTime)
					continue;

				lastTime = time;
				yield return item;
			}
		}

		int IMarketDataStorage.Save(IEnumerable<Message> data)
		{
			return Save(data.Cast<TMessage>());
		}

		void IMarketDataStorage.Delete(IEnumerable<Message> data)
		{
			Delete(data.Cast<TMessage>());
		}

		public void Delete(IEnumerable<TMessage> data)
		{
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			foreach (var group in data.GroupBy(i => _getTime(i).UtcDateTime.Date))
			{
				var date = group.Key;

				lock (GetSync(date))
				{
					var stream = LoadStream(date);

					try
					{
						var metaInfo = GetInfo(stream, date);

						if (metaInfo == null)
							continue;

						var count = metaInfo.Count;

						if (count != group.Count())
						{
							var loadedData = new Dictionary<TId, List<TMessage>>();

							foreach (var item in Serializer.Deserialize(stream, metaInfo))
							{
								var id = _getId(item);

								var loadedItems = loadedData.TryGetValue(id);

								if (loadedItems == null)
								{
									loadedItems = new List<TMessage> { item };
									loadedData.Add(id, loadedItems);
								}
								else
									loadedItems.Add(item);
							}

							foreach (var item in group)
								loadedData.Remove(_getId(item));

							if (loadedData.Count > 0)
							{
								// ?????????????????? ???????????????????????? ????????????, ?????? ?????? ???????????????????? ?????? ???? ?????? ???????????? ????????
								// ?????? ????????????????????????????
								stream = LoadStream(date);

								Save(stream, Serializer.CreateMetaInfo(date),
									loadedData.Values.SelectMany(l => l).ToArray(), true);

								stream.Dispose();
								stream = null;
							}
							else
							{
								((IMarketDataStorage)this).Delete(date);
								stream = null;
							}
						}
						else
						{
							stream.Dispose();
							stream = null;

							((IMarketDataStorage)this).Delete(date);
						}
					}
					catch
					{
						stream?.Dispose();
						throw;
					}
				}
			}
		}

		public IEnumerable<TMessage> Load(DateTime date)
		{
			date = date.Date;

			lock (GetSync(date))
			{
				var stream = LoadStream(date);

				try
				{
					var metaInfo = GetInfo(stream, date);

					if (metaInfo == null)
						return Enumerable.Empty<TMessage>();

					// ???????????? ?????????????????? ??????????, ?????? ?????? ???? ???????? ?????????? ???????????????? ???????????? ?????????? ????????????????????
					//using (stream)
					return Serializer.Deserialize(stream, metaInfo);
				}
				catch (Exception)
				{
					stream.Dispose();
					throw;
				}
			}
		}

		IMarketDataMetaInfo IMarketDataStorage.GetMetaInfo(DateTime date)
		{
			date = date.Date;

			lock (GetSync(date))
			{
				using (var stream = LoadStream(date))
					return GetInfo(stream, date);
			}
		}

		private IMarketDataMetaInfo GetInfo(Stream stream, DateTime date)
		{
			if (stream == Stream.Null)
				return null;

			IMarketDataMetaInfo metaInfo;

			if (Serializer.Format == StorageFormats.Csv)
			{
				metaInfo = _dateMetaInfos.SafeAdd(date, d =>
				{
					var info = Serializer.CreateMetaInfo(date);
					info.Read(stream);
					return info;
				});
			}
			else
			{
				metaInfo = Serializer.CreateMetaInfo(date);
				metaInfo.Read(stream);
			}

			return metaInfo;
		}

		void IMarketDataStorage.Delete(DateTime date)
		{
			date = date.Date;

			lock (GetSync(date))
				Drive.Delete(date);
		}

		IEnumerable<Message> IMarketDataStorage.Load(DateTime date)
		{
			return Load(date);
		}

		DateTimeOffset IMarketDataStorageInfo<TMessage>.GetTime(TMessage data)
		{
			return _getTime(data);
		}

		DateTimeOffset IMarketDataStorageInfo.GetTime(object data)
		{
			return _getTime((TMessage)data);
		}
	}
}