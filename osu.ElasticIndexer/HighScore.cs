// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Dapper.Contrib.Extensions;
using Nest;

namespace osu.ElasticIndexer
{
    [CursorColumn("score_id")]
    [ElasticsearchType(Name = "_doc", IdProperty = nameof(ScoreId))]
    public abstract class HighScore : Model
    {
        [Computed]
        [Ignore]
        public override ulong CursorValue => ScoreId;

        [Computed]
        [Ignore]
        public bool ShouldIndex => Pp.HasValue;

        // Properties ordered in the order they appear in the table.

        [Number(NumberType.Long, Name = "score_id")]
        public ulong ScoreId { get; set; }

        [Number(NumberType.Long, Name = "beatmap_id")]
        public uint BeatmapId { get; set; }

        [Number(NumberType.Long, Name = "user_id")]
        public uint UserId { get; set; }

        [Number(NumberType.Long, Name = "score")]
        public int Score { get; set; }

        [Number(NumberType.Integer, Name = "max_combo")]
        public int MaxCombo { get; set; }

        [Keyword(Name = "rank")]
        public string Rank { get; set; }

        [Number(NumberType.Integer, Name = "count50")]
        public int Count50 { get; set; }

        [Number(NumberType.Integer, Name = "count100")]
        public int Count100 { get; set; }

        [Number(NumberType.Integer, Name = "count300")]
        public int Count300 { get; set; }

        [Number(NumberType.Integer, Name = "countmiss")]
        public int CountMiss { get; set; }

        [Number(NumberType.Integer, Name = "countgeki")]
        public int CountGeki { get; set; }

        [Number(NumberType.Integer, Name = "countkatu")]
        public int CountKatu { get; set; }

        [Boolean(Name = "perfect")]
        public bool Perfect { get; set; }

        [Ignore]
        public int EnabledMods { get; set; }

        [Computed]
        [Keyword(Name = "enabled_mods")]
        public List<string> EnabledModsList => BitsetToList(EnabledMods);

        [Date(Name = "date", Format = "strict_date_optional_time||epoch_millis||yyyy-MM-dd HH:mm:ss")]
        public DateTimeOffset Date { get; set; }

        [Number(NumberType.Float, Name = "pp")]
        public float? Pp { get; set; }

        [Boolean(Name = "replay")]
        public bool Replay { get; set; }

        [Number(NumberType.Short, Name = "hidden")]
        public short Hidden { get; set; }

        [Keyword(Name = "country_acronym")]
        public string CountryAcronym { get; set; }

        // TODO: mod-related; move out.
        public static readonly Dictionary<int, Tuple<string, int?>> AVAILABLE_MODS = new Dictionary<int, Tuple<string, int?>>
        {
            { 0, Tuple.Create<string, int?>("NF", null) },
            { 1, Tuple.Create<string, int?>("EZ", null) },
            { 3, Tuple.Create<string, int?>("HD", null) },
            { 20, Tuple.Create<string, int?>("FI", null) },
            { 4, Tuple.Create<string, int?>("HR", null) },
            { 9, Tuple.Create<string, int?>("NC", 6) },
            { 6, Tuple.Create<string, int?>("DT", null) },
            { 7, Tuple.Create<string, int?>("Relax", null) },
            { 8, Tuple.Create<string, int?>("HT", null) },
            { 10, Tuple.Create<string, int?>("FL", null) },
            { 12, Tuple.Create<string, int?>("SO", null) },
            { 13, Tuple.Create<string, int?>("AP", null) },
            { 14, Tuple.Create<string, int?>("PF", 5) },
            { 5, Tuple.Create<string, int?>("SD", null) },
            { 2, Tuple.Create<string, int?>("TD", null) },

            // mania keys (converts)
            { 15, Tuple.Create<string, int?>("4K", null) },
            { 16, Tuple.Create<string, int?>("5K", null) },
            { 17, Tuple.Create<string, int?>("6K", null) },
            { 18, Tuple.Create<string, int?>("7K", null) },
            { 19, Tuple.Create<string, int?>("8K", null) },
            { 24, Tuple.Create<string, int?>("9K", null) }
        };

        public static List<string> BitsetToList(int bitset)
        {
            var mods = new Dictionary<int, string>();
            var impliedIds = new List<int>();

            foreach (var key in AVAILABLE_MODS.Keys)
            {
                if ((bitset & (1 << key)) == 0) continue;

                var tuple = AVAILABLE_MODS[key];
                if (tuple.Item2.HasValue) impliedIds.Add(tuple.Item2.Value);

                mods[key] = tuple.Item1;

                foreach (var impliedId in impliedIds) mods.Remove(impliedId);
            }

            return mods.Values.ToList();
        }

        public static int GetRulesetId<T>() where T : HighScore
        {
            return typeof(T).GetCustomAttributes<RulesetIdAttribute>().First().Id;
        }

        public static Type GetTypeFromModeString(string mode)
        {
            var className = $"{typeof(HighScore).Namespace}.HighScore{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(mode)}";

            return Type.GetType(className, true);
        }
    }
}
