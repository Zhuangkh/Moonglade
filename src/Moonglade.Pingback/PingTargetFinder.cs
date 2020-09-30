﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;

namespace Moonglade.Pingback
{
    public interface IPingTargetFinder
    {
        Task<(Guid Id, string Title)> GetPostIdTitle(string url, IDbConnection conn);
        Task<bool> HasAlreadyBeenPinged(Guid postId, string sourceUrl, string sourceIp, IDbConnection conn);
    }

    public class PingTargetFinder : IPingTargetFinder
    {
        public async Task<(Guid Id, string Title)> GetPostIdTitle(string url, IDbConnection conn)
        {
            var slugInfo = GetSlugInfoFromPostUrl(url);
            var sql = "SELECT p.Id, p.Title FROM Post p " +
                      "WHERE p.IsPublished = '1' " +
                      "AND p.IsDeleted = '0'" +
                      "AND p.Slug = @slug " +
                      "AND YEAR(p.PubDateUtc) = @year " +
                      "AND MONTH(p.PubDateUtc) = @month " +
                      "AND DAY(p.PubDateUtc) = @day";
            var p = await conn.QueryFirstOrDefaultAsync<(Guid Id, string Title)>(sql, new
            {
                slug = slugInfo.Slug,
                year = slugInfo.PubDate.Year,
                month = slugInfo.PubDate.Month,
                day = slugInfo.PubDate.Day
            });
            return p;
        }

        public async Task<bool> HasAlreadyBeenPinged(Guid postId, string sourceUrl, string sourceIp, IDbConnection conn)
        {
            var sql = $"SELECT TOP 1 1 FROM {nameof(PingbackHistory)} ph " +
                      $"WHERE ph.TargetPostId = @postId " +
                      $"AND ph.SourceUrl = @sourceUrl " +
                      $"AND ph.SourceIp = @sourceIp";
            var result = await conn.ExecuteScalarAsync<int>(sql, new { postId, sourceUrl, sourceIp });
            return result == 1;
        }

        private static (string Slug, DateTime PubDate) GetSlugInfoFromPostUrl(string url)
        {
            var blogSlugRegex = new Regex(@"^https?:\/\/.*\/post\/(?<yyyy>\d{4})\/(?<MM>\d{1,12})\/(?<dd>\d{1,31})\/(?<slug>.*)$");
            Match match = blogSlugRegex.Match(url);
            if (!match.Success)
            {
                throw new FormatException("Invalid Slug Format");
            }

            int year = int.Parse(match.Groups["yyyy"].Value);
            int month = int.Parse(match.Groups["MM"].Value);
            int day = int.Parse(match.Groups["dd"].Value);
            string slug = match.Groups["slug"].Value;
            var date = new DateTime(year, month, day);

            return (slug, date);
        }
    }
}