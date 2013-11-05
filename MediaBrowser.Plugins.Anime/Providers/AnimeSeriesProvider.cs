﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Plugins.Anime.Providers.AniDB;
using MediaBrowser.Plugins.Anime.Providers.AniList;
using MediaBrowser.Plugins.Anime.Providers.MyAnimeList;

namespace MediaBrowser.Plugins.Anime.Providers
{
    public class AnimeSeriesProvider : BaseMetadataProvider
    {
        private readonly IEnumerable<ISeriesProvider> _allProviders;
        private readonly AniDbSeriesProvider _aniDbProvider;
        private readonly AniListSeriesProvider _aniListProvider;
        private readonly MalSeriesProvider _malProvider;

        public AnimeSeriesProvider(ILogManager logManager, IServerConfigurationManager configurationManager, IApplicationPaths appPaths, IHttpClient httpClient)
            : base(logManager, configurationManager)
        {
            _aniDbProvider = new AniDbSeriesProvider(logManager.GetLogger("AniDB"), configurationManager, appPaths, httpClient);
            _malProvider = new MalSeriesProvider(new MalSeriesDownloader(appPaths, logManager.GetLogger("MalDownloader")), logManager.GetLogger("MyAnimeList"));
            _aniListProvider = new AniListSeriesProvider(new AniListSeriesDownloader(appPaths, logManager.GetLogger("AniListDownloader")), logManager.GetLogger("AniList"), configurationManager);

            _allProviders = new ISeriesProvider[]
            {
                _aniDbProvider,
                _aniListProvider,
                _malProvider
            };
        }

        public override bool EnforceDontFetchMetadata
        {
            get { return false; }
        }

        public override MetadataProviderPriority Priority
        {
            get { return MetadataProviderPriority.Third; }
        }

        public override bool RequiresInternet
        {
            get { return _allProviders.Any(p => p.RequiresInternet); }
        }

        protected override string ProviderVersion
        {
            get { return "1"; }
        }

        public override bool Supports(BaseItem item)
        {
            return item is Series;
        }

        protected override bool NeedsRefreshBasedOnCompareDate(BaseItem item, BaseProviderInfo providerInfo)
        {
            if (_allProviders.Any(p => p.NeedsRefreshBasedOnCompareDate(item, providerInfo)))
                return true;

            return base.NeedsRefreshBasedOnCompareDate(item, providerInfo);
        }

        public override async Task<bool> FetchAsync(BaseItem item, bool force, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var series = (Series) item;

            // get anidb info
            SeriesInfo anidb = await _aniDbProvider.FindSeriesInfo(series, cancellationToken);
            AddProviders(series, anidb.ExternalProviders);

            // get anilist info
            SeriesInfo anilist = await _aniListProvider.FindSeriesInfo(series, cancellationToken);
            AddProviders(series, anilist.ExternalProviders);

            // get mal info
            SeriesInfo mal = await _malProvider.FindSeriesInfo(series, cancellationToken);
            AddProviders(series, mal.ExternalProviders);

            if (!series.DontFetchMeta)
            {
                MergeSeriesInfo(series, anidb, anilist, mal);
            }

            SetLastRefreshed(item, DateTime.UtcNow);
            return true;
        }

        private void MergeSeriesInfo(Series item, SeriesInfo anidb, SeriesInfo anilist, SeriesInfo mal)
        {
            if (!item.LockedFields.Contains(MetadataFields.Name))
                item.Name = anidb.Name ?? anilist.Name ?? mal.Name ?? item.Name;

            if (!item.LockedFields.Contains(MetadataFields.Overview))
                item.Overview = item.Overview ?? anilist.Description ?? mal.Description ?? anidb.Description;

            if (!item.LockedFields.Contains(MetadataFields.Cast))
            {
                IEnumerable<PersonInfo> people = SelectCollection(anidb.People, anilist.People, mal.People, item.People.ToArray());
                item.People.Clear();
                foreach (PersonInfo person in people)
                    item.AddPerson(person);
            }

            if (!item.LockedFields.Contains(MetadataFields.OfficialRating))
                item.OfficialRating = item.OfficialRating ?? anidb.ContentRating ?? anilist.ContentRating ?? mal.ContentRating;

            if (!item.LockedFields.Contains(MetadataFields.Runtime))
                item.RunTimeTicks = anidb.RunTimeTicks ?? item.RunTimeTicks ?? anilist.RunTimeTicks ?? mal.RunTimeTicks;

            if (!item.LockedFields.Contains(MetadataFields.Genres))
            {
                IEnumerable<string> genres = SelectCollection(mal.Genres, anilist.Genres, item.Genres.ToArray(), anidb.Genres);
                item.Genres.Clear();
                foreach (string genre in genres)
                    item.AddGenre(genre);

                item.AddGenre("Animation");
            }

            if (!item.LockedFields.Contains(MetadataFields.Studios))
            {
                IEnumerable<string> studios = SelectCollection(anidb.Studios, anilist.Studios, mal.Studios, item.Studios.ToArray());
                item.Studios.Clear();
                foreach (string studio in studios)
                    item.AddStudio(studio);
            }

            item.PremiereDate = anidb.StartDate ?? anilist.StartDate ?? mal.StartDate ?? item.PremiereDate;
            item.EndDate = anidb.EndDate ?? anilist.EndDate ?? mal.EndDate ?? item.EndDate;
            item.Status = item.EndDate != null ? SeriesStatus.Ended : SeriesStatus.Continuing;
            item.AirTime = anidb.AirTime ?? anilist.AirTime ?? mal.AirTime ?? item.AirTime;
            item.AirDays = SelectCollection(anidb.AirDays, anilist.AirDays, mal.AirDays, item.AirDays).ToList();

            if (item.ProductionYear == null && item.PremiereDate != null)
            {
                item.ProductionYear = item.PremiereDate.Value.Year;
            }

            SeriesInfo mostVoted = (new[] {anidb, mal, anilist}).OrderByDescending(info => info.VoteCount ?? 0).First();
            if (mostVoted.VoteCount > 0)
            {
                item.CommunityRating = mostVoted.CommunityRating;
                item.VoteCount = mostVoted.VoteCount;
            }
        }

        private IEnumerable<T> SelectCollection<T>(params IEnumerable<T>[] items)
        {
            return items.FirstOrDefault(l => l != null && l.Any()) ?? new List<T>();
        }

        private void AddProviders(BaseItem item, Dictionary<string, string> providers)
        {
            foreach (var provider in providers)
            {
                item.ProviderIds[provider.Key] = provider.Value;
            }
        }
    }
}