﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moonglade.Model;

namespace Moonglade.Pingback
{
    public class PingbackService : IPingbackService
    {
        private readonly ILogger<PingbackService> _logger;
        private readonly IConfiguration _configuration;

        private string _sourceUrl;
        private string _targetUrl;

        public PingbackService(
            ILogger<PingbackService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<PingbackResponse> ProcessReceivedPayloadAsync(HttpContext context, Action<PingbackHistory> pingSuccessAction)
        {
            try
            {
                var connStr = _configuration.GetConnectionString(Constants.DbConnectionName);
                await using var conn = new SqlConnection(connStr);

                var ip = context.Connection.RemoteIpAddress?.ToString();
                var requestBody = await new StreamReader(context.Request.Body, Encoding.Default).ReadToEndAsync();

                var response = ValidatePingRequest(requestBody);
                if (response != PingbackValidationResult.Valid) return PingbackResponse.InvalidPingRequest;

                _logger.LogInformation($"Pingback attempt from '{ip}' is valid");
                _logger.LogInformation($"Processing Pingback from: {_sourceUrl} to {_targetUrl}");

                var pingRequest = await ExamineSourceAsync();
                if (null == pingRequest) return PingbackResponse.InvalidPingRequest;

                var postResponse = await GetPostIdTitle(pingRequest.TargetUrl, conn);
                if (postResponse.Id == Guid.Empty)
                {
                    _logger.LogError($"Can not get post id and title for url '{pingRequest.TargetUrl}'");
                    return PingbackResponse.Error32TargetUriNotExist;
                }
                _logger.LogInformation($"Post '{postResponse.Id}:{postResponse.Title}' is found for ping.");

                var pinged = await HasAlreadyBeenPinged(postResponse.Id, pingRequest.SourceUrl, ip, conn);
                if (pinged) return PingbackResponse.Error48PingbackAlreadyRegistered;

                if (pingRequest.SourceDocumentInfo.SourceHasLink && !pingRequest.SourceDocumentInfo.ContainsHtml)
                {
                    _logger.LogInformation("Adding received pingback...");
                    var domain = GetDomain(_sourceUrl);

                    var obj = new PingbackHistory
                    {
                        Id = Guid.NewGuid(),
                        PingTimeUtc = DateTime.UtcNow,
                        Domain = domain,
                        SourceUrl = _sourceUrl,
                        SourceTitle = pingRequest.SourceDocumentInfo.Title,
                        TargetPostId = postResponse.Id,
                        TargetPostTitle = postResponse.Title,
                        SourceIp = ip
                    };

                    await SavePingbackRecordAsync(obj, conn);
                    pingSuccessAction(obj);

                    return PingbackResponse.Success;
                }

                if (!pingRequest.SourceDocumentInfo.SourceHasLink)
                {
                    _logger.LogError("Pingback error: The source URI does not contain a link to the target URI, and so cannot be used as a source.");
                    return PingbackResponse.Error17SourceNotContainTargetUri;
                }
                _logger.LogWarning("Spam detected on current Pingback...");
                return PingbackResponse.SpamDetectedFakeNotFound;

            }
            catch (Exception e)
            {
                _logger.LogError(e, nameof(ProcessReceivedPayloadAsync));
                return PingbackResponse.GenericError;
            }
        }

        public async Task<IEnumerable<PingbackHistory>> GetPingbackHistoryAsync()
        {
            try
            {
                var connStr = _configuration.GetConnectionString(Constants.DbConnectionName);
                await using var conn = new SqlConnection(connStr);
                var sql = $"SELECT ph.{nameof(PingbackHistory.Id)}, " +
                          $"ph.{nameof(PingbackHistory.Domain)}, " +
                          $"ph.{nameof(PingbackHistory.SourceUrl)}, " +
                          $"ph.{nameof(PingbackHistory.SourceTitle)}, " +
                          $"ph.{nameof(PingbackHistory.TargetPostId)}, " +
                          $"ph.{nameof(PingbackHistory.TargetPostTitle)}, " +
                          $"ph.{nameof(PingbackHistory.PingTimeUtc)} " +
                          $"FROM {nameof(PingbackHistory)} ph";

                var list = await conn.QueryAsync<PingbackHistory>(sql);
                return list;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error {nameof(GetPingbackHistoryAsync)}");
                throw;
            }
        }

        public async Task DeletePingbackHistory(Guid id)
        {
            try
            {
                var connStr = _configuration.GetConnectionString(Constants.DbConnectionName);
                await using var conn = new SqlConnection(connStr);
                var sql = $"DELETE FROM {nameof(PingbackHistory)} WHERE Id = @id";
                await conn.ExecuteAsync(sql, new { id });
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error {nameof(id)}");
                throw;
            }
        }

        private async Task<PingRequest> ExamineSourceAsync()
        {
            try
            {
                var regexHtml = new Regex(
                    @"</?\w+((\s+\w+(\s*=\s*(?:"".*?""|'.*?'|[^'"">\s]+))?)+\s*|\s*)/?>",
                    RegexOptions.Singleline | RegexOptions.Compiled);

                var regexTitle = new Regex(
                    @"(?<=<title.*>)([\s\S]*)(?=</title>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var html = await httpClient.GetStringAsync(_sourceUrl);
                var title = regexTitle.Match(html).Value.Trim();
                _logger.LogInformation($"ExamineSourceAsync:title: {title}");

                var containsHtml = regexHtml.IsMatch(title);
                _logger.LogInformation($"ExamineSourceAsync:containsHtml: {containsHtml}");

                var sourceHasLink = html.ToUpperInvariant().Contains(_targetUrl.ToUpperInvariant());
                _logger.LogInformation($"ExamineSourceAsync:sourceHasLink: {sourceHasLink}");

                var pingRequest = new PingRequest
                {
                    SourceDocumentInfo = new SourceDocumentInfo
                    {
                        Title = title,
                        ContainsHtml = containsHtml,
                        SourceHasLink = sourceHasLink
                    },
                    TargetUrl = _targetUrl,
                    SourceUrl = _sourceUrl
                };

                return pingRequest;
            }
            catch (WebException ex)
            {
                _logger.LogError(ex, nameof(ExamineSourceAsync));
                return new PingRequest
                {
                    SourceDocumentInfo = new SourceDocumentInfo
                    {
                        SourceHasLink = false
                    },
                    SourceUrl = _sourceUrl
                };
            }
        }

        private static string GetDomain(string sourceUrl)
        {
            var start = sourceUrl.IndexOf("://", StringComparison.Ordinal) + 3;
            var stop = sourceUrl.IndexOf("/", start, StringComparison.Ordinal);
            return sourceUrl[start..stop].Replace("www.", string.Empty);
        }

        private async Task SavePingbackRecordAsync(PingbackHistory request, IDbConnection conn)
        {
            var sql = $"INSERT INTO {nameof(PingbackHistory)}" +
                      $"(Id, Domain, SourceUrl, SourceTitle, SourceIp, TargetPostId, PingTimeUtc, TargetPostTitle) " +
                      $"VALUES (@id, @domain, @sourceUrl, @sourceTitle, @targetPostId, @pingTimeUtc, @targetPostTitle)";
            await conn.ExecuteAsync(sql, request);
        }

        private async Task<(Guid Id, string Title)> GetPostIdTitle(string url, IDbConnection conn)
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

        private async Task<bool> HasAlreadyBeenPinged(Guid postId, string sourceUrl, string sourceIp, IDbConnection conn)
        {
            var sql = $"SELECT TOP 1 1 FROM {nameof(PingbackHistory)} ph " +
                      $"WHERE ph.TargetPostId = @postId " +
                      $"AND ph.SourceUrl = @sourceUrl " +
                      $"AND ph.SourceIp = @sourceIp";
            var result = await conn.ExecuteScalarAsync<int>(sql, new { postId, sourceUrl, sourceIp });
            return result == 1;
        }

        private PingbackValidationResult ValidatePingRequest(string requestBody)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    throw new ArgumentNullException(nameof(requestBody));
                }

                _logger.LogInformation($"Pingback received xml: {requestBody}");

                if (!requestBody.Contains("<methodName>pingback.ping</methodName>"))
                {
                    return PingbackValidationResult.TerminatedMethodNotFound;
                }

                var doc = new XmlDocument();
                doc.LoadXml(requestBody);

                var list = doc.SelectNodes("methodCall/params/param/value/string") ??
                           doc.SelectNodes("methodCall/params/param/value");

                if (list == null)
                {
                    _logger.LogWarning("Could not find Pingback sourceUrl and targetUrl, request has been terminated.");
                    return PingbackValidationResult.TerminatedUrlNotFound;
                }

                _sourceUrl = list[0].InnerText.Trim();
                _targetUrl = list[1].InnerText.Trim();

                return PingbackValidationResult.Valid;
            }
            catch (Exception e)
            {
                _logger.LogError(e, nameof(ValidatePingRequest));
                return PingbackValidationResult.GenericError;
            }
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