using System;
using System.Collections.Generic;
using System.Linq;
using MyModManager.Models;

namespace MyModManager.Helpers;

public enum FavoriteFilter
{
    All,
    Favorites,
    NonFavorites
}

public enum ContentRatingFilter
{
    All,
    Sfw,
    Nsfw
}

public static class FavoriteGrouping
{
    public const string SceneTagAll = "";
    public const string SceneTagUntagged = "__untagged__";

    public static List<(string Category, List<(string DisplayName, List<ManagedMod> Mods)> Names)> Build(
        IReadOnlyList<ManagedMod> mods,
        string search,
        FavoriteFilter favoriteFilter = FavoriteFilter.All,
        ContentRatingFilter ratingFilter = ContentRatingFilter.All,
        string? sceneTag = null)
    {
        IEnumerable<ManagedMod> query = mods;
        query = favoriteFilter switch
        {
            FavoriteFilter.Favorites => query.Where(m => m.IsFavorite),
            FavoriteFilter.NonFavorites => query.Where(m => !m.IsFavorite),
            _ => query
        };

        query = ratingFilter switch
        {
            ContentRatingFilter.Sfw => query.Where(m => m.Rating == ContentRating.Sfw),
            ContentRatingFilter.Nsfw => query.Where(m => m.Rating == ContentRating.Nsfw),
            _ => query
        };

        if (string.Equals(sceneTag, SceneTagUntagged, StringComparison.Ordinal))
        {
            query = query.Where(m => m.Tags == null || m.Tags.Count == 0);
        }
        else if (!string.IsNullOrEmpty(sceneTag))
        {
            query = query.Where(m => m.Tags != null && m.Tags.Any(t => t.Equals(sceneTag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m =>
                (m.DisplayName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.ModName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.ShortcutName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.CategoryName ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (m.Tags != null && m.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase))));
        }

        return query
            .GroupBy(m => string.IsNullOrEmpty(m.CategoryName) ? "Default" : m.CategoryName)
            .OrderBy(g => g.Key)
            .Select(cat => (
                cat.Key,
                cat.GroupBy(m => m.DisplayName ?? string.Empty)
                    .OrderBy(g => g.Key)
                    .Select(n => (n.Key, n.ToList()))
                    .ToList()
            ))
            .ToList();
    }

    public static string FormatMetaLabels(ManagedMod mod)
    {
        var parts = new List<string>();
        if (mod.Rating == ContentRating.Nsfw)
            parts.Add("NSFW");
        else if (mod.Rating == ContentRating.Sfw)
            parts.Add("SFW");

        if (mod.IsTemp) parts.Add("Temp");
        if (mod.AutoEmoteSync) parts.Add("Sync");
        if (mod.Tags != null)
        {
            foreach (var tag in mod.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    parts.Add(tag);
            }
        }

        return string.Join("][", parts);
    }
}
