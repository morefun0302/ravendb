﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NLog;
using Raven.Abstractions.Data;
using Raven.Database.Storage;
using System.Linq;

namespace Raven.Database.Indexing
{
	public abstract class AbstractIndexingExecuter
	{
		protected WorkContext context;
		protected TaskScheduler scheduler;
		protected static readonly Logger log = LogManager.GetCurrentClassLogger();
		protected ITransactionalStorage transactionalStorage;
		protected int workCounter;
		protected int lastFlushedWorkCounter;
		private int numberOfItemsToIndexInSingleBatch;
		protected int lastAmountOfItemsToIndex;

		protected AbstractIndexingExecuter(ITransactionalStorage transactionalStorage, WorkContext context, TaskScheduler scheduler)
		{
			this.transactionalStorage = transactionalStorage;
			this.context = context;
			this.scheduler = scheduler;
			NumberOfItemsToIndexInSingleBatch = context.Configuration.InitialNumberOfItemsToIndexInSingleBatch;
		}

		public int NumberOfItemsToIndexInSingleBatch
		{
			get { return numberOfItemsToIndexInSingleBatch; }
			set
			{
				context.CurrentNumberOfItemsToIndexInSingleBatch  = numberOfItemsToIndexInSingleBatch = value;
			}
		}

		protected void AutoThrottleBatchSize(int amountOfItemsToIndex)
		{
			var lastTime = lastAmountOfItemsToIndex;

			lastAmountOfItemsToIndex = amountOfItemsToIndex;
			if(amountOfItemsToIndex < NumberOfItemsToIndexInSingleBatch) // we didn't have a lot of work to do
			{
				// we are at the configured default, nothing to do
				if (NumberOfItemsToIndexInSingleBatch == context.Configuration.InitialNumberOfItemsToIndexInSingleBatch)
					return;
				

				// we were above the max the last time, we can't reduce the work load now
				if (lastTime > NumberOfItemsToIndexInSingleBatch)
					return;

				// we have had a couple of times were we didn't get to the current max, so we can probably
				// reduce the max again now.

				NumberOfItemsToIndexInSingleBatch = Math.Max(context.Configuration.InitialNumberOfItemsToIndexInSingleBatch,
																NumberOfItemsToIndexInSingleBatch / 2);
			}
			// in the previous run, we also hit the current limit, we need to check if we can increase the max batch size
			else if (lastTime >= NumberOfItemsToIndexInSingleBatch)
			{
				if(context.Configuration.AvailablePhysicalMemoryInMegabytes >= context.Configuration.AvailableMemoryForRaisingIndexBatchSizeLimit)
				{
					NumberOfItemsToIndexInSingleBatch = Math.Min(context.Configuration.MaxNumberOfItemsToIndexInSingleBatch,
																 NumberOfItemsToIndexInSingleBatch * 2);
				}
				else // we are using too much memory, let us use a little lett next time...
				{
					NumberOfItemsToIndexInSingleBatch = Math.Max(context.Configuration.InitialNumberOfItemsToIndexInSingleBatch,
															NumberOfItemsToIndexInSingleBatch / 2);
				}
			}
		}

		public void Execute()
		{
			while (context.DoWork)
			{
				var foundWork = false;
				try
				{
					foundWork = ExecuteIndexing();
				}
				catch (Exception e)
				{
					log.ErrorException("Failed to execute indexing", e);
				}
				if (foundWork == false)
				{
					context.WaitForWork(TimeSpan.FromHours(1), ref workCounter, FlushIndexes);
				}
				else // notify the tasks executer that it has work to do
				{
					context.NotifyAboutWork();
				}
			}
		}

		private void FlushIndexes()
		{
			if (lastFlushedWorkCounter == workCounter || context.DoWork == false)
				return;
			lastFlushedWorkCounter = workCounter;
			FlushAllIndexes();
		}

		protected abstract void FlushAllIndexes();

		protected bool ExecuteIndexing()
		{
			var indexesToWorkOn = new List<IndexToWorkOn>();
			transactionalStorage.Batch(actions =>
			{
				foreach (var indexesStat in actions.Indexing.GetIndexesStats().Where(IsValidIndex))
				{
					var failureRate = actions.Indexing.GetFailureRate(indexesStat.Name);
					if (failureRate.IsInvalidIndex)
					{
						log.Info("Skipped indexing documents for index: {0} because failure rate is too high: {1}",
									   indexesStat.Name,
									   failureRate.FailureRate);
						continue;
					}
					if (IsIndexStale(indexesStat, actions) == false)
						continue;
					indexesToWorkOn.Add(GetIndexToWorkOn(indexesStat));
				}
			});

			if (indexesToWorkOn.Count == 0)
				return false;

			if (context.Configuration.MaxNumberOfParallelIndexTasks == 1)
				ExecuteIndexingWorkOnSingleThread(indexesToWorkOn);
			else
				ExecuteIndexingWorkOnMultipleThreads(indexesToWorkOn);

			return true;
		}

		protected abstract IndexToWorkOn GetIndexToWorkOn(IndexStats indexesStat);

		protected abstract bool IsIndexStale(IndexStats indexesStat, IStorageActionsAccessor actions);

		protected abstract void ExecuteIndexingWorkOnMultipleThreads(IList<IndexToWorkOn> indexesToWorkOn);

		protected abstract void ExecuteIndexingWorkOnSingleThread(IList<IndexToWorkOn> indexesToWorkOn);


		protected abstract bool IsValidIndex(IndexStats indexesStat);

		public class IndexToWorkOn
		{
			public string IndexName { get; set; }
			public Guid LastIndexedEtag { get; set; }

			public override string ToString()
			{
				return string.Format("IndexName: {0}, LastIndexedEtag: {1}", IndexName, LastIndexedEtag);
			}
		}

		protected class ComparableByteArray : IComparable<ComparableByteArray>, IComparable
		{
			private readonly byte[] inner;

			public ComparableByteArray(byte[] inner)
			{
				this.inner = inner;
			}

			public int CompareTo(ComparableByteArray other)
			{
				if (inner.Length != other.inner.Length)
					return inner.Length - other.inner.Length;
				for (int i = 0; i < inner.Length; i++)
				{
					if (inner[i] != other.inner[i])
						return inner[i] - other.inner[i];
				}
				return 0;
			}

			public int CompareTo(object obj)
			{
				return CompareTo((ComparableByteArray)obj);
			}

			public Guid ToGuid()
			{
				return new Guid(inner);
			}

			public override string ToString()
			{
				return ToGuid().ToString();
			}
		}

	}
}