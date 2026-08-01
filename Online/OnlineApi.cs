using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Entrys;
using System.Data;
using System.Text;
using System.Linq;
using IO = System.IO;
using Shared.Services.RxEnumerate;
using Shared;
using Shared.Services;
using System.Text.RegularExpressions;
using System;
using System.Web;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using Shared.Models.Base;
using System.Collections.Generic;
using Shared.Services.Utilities;
using Shared.Services.BestBalanser;

namespace Online;

public class OnlineApiController : BaseController
{
    record EventLinkItem(string code, int index, bool work, string plugin = null, int qualityScore = 0, double speedScore = 0, bool deprioritize = false);

    #region online.js
    [HttpGet]
    [AllowAnonymous]
    [Route("online.js")]
    [Route("online/js/{token}")]
    public ContentResult Online(string token)
    {
        SetHeadersNoCache();

        var init = ModInit.conf;
        var apr = init.appReplace;

        string memKey = $"online.js:{apr?.Count ?? 0}:{init.version}:{init.description}:{init.apn}:{host}:{init.spider}:{init.component}:{init.name}:{init.spiderName}";
        if (!memoryCache.TryGetValue(memKey, out (string file, string filecleaer) cache))
        {
            cache.file = FileCache.ReadAllText($"{ModInit.modpath}/plugin.js", "online.js", false)
                .Replace("{rch_websoket}", FileCache.ReadAllText("plugins/rch_nws.js", "rch_nws.js", false));

            #region appReplace
            if (apr != null)
            {
                foreach (var r in apr)
                {
                    string val = r.Value;
                    if (val.StartsWith("file:"))
                        val = IO.File.ReadAllText(val.Substring(5));

                    cache.file = Regex.Replace(cache.file, r.Key, val, RegexOptions.IgnoreCase);
                }
            }
            #endregion

            if (!init.version)
            {
                cache.file = Regex.Replace(cache.file, "version: \\'[^\\']+\\'", "version: ''")
                                  .Replace("manifst.name, \" v\"", "manifst.name, \" \"");
            }

            if (init.description != "Плагин для просмотра онлайн сериалов и фильмов")
                cache.file = Regex.Replace(cache.file, "description: \\'([^\\']+)?\\'", $"description: '{init.description}'");

            if (init.apn != null)
                cache.file = Regex.Replace(cache.file, "apn: \\'([^\\']+)?\\'", $"apn: '{init.apn}'");

            var bulder = new StringBuilder(cache.file);

            if (!init.spider)
            {
                bulder = bulder.Replace("addSourceSearch('Spider', 'spider');", "")
                               .Replace("addSourceSearch('Anime', 'spider/anime');", "");
            }

            if (init.component != "lampac")
            {
                bulder = bulder.Replace("component: 'lampac'", $"component: '{init.component}'")
                               .Replace("'lampac', component", $"'{init.component}', component")
                               .Replace("window.lampac_plugin", $"window.{init.component}_plugin");
            }

            if (init.name != "Lampac")
                bulder = bulder.Replace("name: 'Lampac'", $"name: '{init.name}'");

            if (CoreInit.conf.kit.aesgcmkeyName != null)
                bulder = bulder.Replace("aesgcmkey", CoreInit.conf.kit.aesgcmkeyName);

            if (init.spiderName != "Spider")
            {
                bulder = bulder.Replace("addSourceSearch('Spider'", $"addSourceSearch('{init.spiderName}'")
                               .Replace("addSourceSearch('Anime'", $"addSourceSearch('{init.spiderName} - Anime'");
            }

            bulder = bulder
                .Replace("{invc-rch}", FileCache.ReadAllText("plugins/invc-rch.js", "invc-rch.js", false))
                .Replace("{invc-rch_nws}", FileCache.ReadAllText("plugins/invc-rch_nws.js", "invc-rch_nws.js", false))
                .Replace("{player-inner}", string.Empty)
                .Replace("{localhost}", host);

            cache.file = bulder.ToString();
            cache.filecleaer = cache.file.Replace("{token}", string.Empty);

            memoryCache.Set(memKey, cache, DateTime.Now.AddMinutes(10));
        }

        if (EventListener.AppReplace != null)
        {
            string source = cache.file;

            foreach (Func<string, EventAppReplace, string> handler in EventListener.AppReplace.GetInvocationList())
                source = handler.Invoke("online", new EventAppReplace(source, token, null, host, requestInfo, HttpContext.Request));

            return Content(source.Replace("{token}", HttpUtility.UrlEncode(token)), "application/javascript; charset=utf-8");
        }

        return Content(token != null ? cache.file.Replace("{token}", HttpUtility.UrlEncode(token)) : cache.filecleaer, "application/javascript; charset=utf-8");
    }
    #endregion


    #region externalids
    /// <summary>
    /// imdb_id, kinopoisk_id
    /// </summary>
    static ConcurrentDictionary<string, string> externalids = null;

    [HttpGet]
    [Route("externalids")]
    async public Task<ActionResult> Externalids(string id, string imdb_id, long kinopoisk_id, int serial)
    {
        #region cache
        string memKey = $"OnlineApi:externalids:{id}:{imdb_id}:{kinopoisk_id}:{serial}";
        if (memoryCache.TryGetValue(memKey, out string jsonResult))
            return Content(jsonResult, "application/json; charset=utf-8");
        #endregion

        if (externalids == null)
            externalids = JsonConvert.DeserializeObject<ConcurrentDictionary<string, string>>(IO.File.ReadAllText("data/externalids.json"));

        #region KP_
        if (id != null && id.StartsWith("KP_"))
        {
            string _kp = id.Substring(0, 3);
            foreach (var eid in externalids)
            {
                if (eid.Value == _kp && !string.IsNullOrEmpty(eid.Key))
                {
                    imdb_id = eid.Key;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(imdb_id))
            {
                return Json(new { imdb_id, kinopoisk_id = _kp });
            }
            else
            {
                string mkey = $"externalids:KP_:{_kp}";
                if (!hybridCache.TryGetValue(mkey, out string _imdbid))
                {
                    var bearer = HeadersModel.Init(
                        ("Authorization", $"Bearer 04941a9a3ca3ac16e2b4327347bbc1"),
                        ("Accept", "application/json")
                    );

                    await Http.GetSpan($"https://apbugall.org/v2/movies/search?kp={_kp}", timeoutSeconds: 5, headers: bearer, spanAction: json =>
                    {
                        _imdbid = Rx.Match(json, "\"id_imdb\":\"(tt[^\"]+)\"");
                    });

                    if (string.IsNullOrEmpty(_imdbid))
                        hybridCache.Set(mkey, string.Empty, DateTime.Now.AddHours(1));
                    else
                        hybridCache.Set(mkey, _imdbid, DateTime.Now.AddHours(8));
                }

                return Json(new { imdb_id = _imdbid, kinopoisk_id = _kp });
            }
        }
        #endregion

        #region getAlloha / getVSDN / getTabus
        async Task<string> getAlloha(string imdb)
        {
            string kpid = null;

            var bearer = HeadersModel.Init(
                ("Authorization", $"Bearer 04941a9a3ca3ac16e2b4327347bbc1"),
                ("Accept", "application/json")
            );

            await Http.GetSpan($"https://apbugall.org/v2/movies/search?imdb={imdb}", timeoutSeconds: 5, headers: bearer, spanAction: json =>
            {
                kpid = Rx.Match(json, "\"ids\":{\"kp\":([0-9]+),");
            });

            if (!string.IsNullOrEmpty(kpid) && kpid != "0" && kpid != "null")
                return kpid;

            return null;
        }

        async Task<string> getTabus(string imdb)
        {
            string kpid = null;

            await Http.GetSpan("https://api.bhcesh.me/franchise/details?token=d39edcf2b6219b6421bffe15dde9f1b3&imdb_id=" + imdb.Remove(0, 2), timeoutSeconds: 5, spanAction: json =>
            {
                kpid = Rx.Match(json, "\"kinopoisk_id\":\"?([0-9]+)\"?");
            });

            if (!string.IsNullOrEmpty(kpid) && kpid != "0" && kpid != "null")
                return kpid;

            return null;
        }

        //async Task<string> getVSDN(string imdb)
        //{
        //    //long? res = Lumex.database.FirstOrDefault(i => i.imdb_id == imdb)?.kinopoisk_id;
        //    //if (res > 0)
        //    //    return res.ToString();

        //    if (string.IsNullOrEmpty(ModInit.siteConf.VideoCDN.token) || string.IsNullOrEmpty(ModInit.siteConf.VideoCDN.iframehost))
        //        return null;

        //    ProxyManager proxyManager = ModInit.siteConf.VideoCDN.useproxy
        //        ? new ProxyManager("videocdn", ModInit.siteConf.VideoCDN)
        //        : null;

        //    string kpid = null;

        //    await Http.GetSpan($"{ModInit.siteConf.VideoCDN.iframehost}/api/short?api_token={ModInit.siteConf.VideoCDN.token}&imdb_id={imdb}", json =>
        //    {
        //        string kp = Rx.Groups(json, "\"kp_id\":\"?([0-9]+)\"?")[1].Value;
        //        if (!string.IsNullOrEmpty(kpid) && kpid != "0" && kpid != "null")
        //            kpid = kp;

        //    }, timeoutSeconds: 10, proxy: proxyManager?.Get());

        //    return kpid;
        //}
        #endregion

        #region get imdb_id
        if (string.IsNullOrWhiteSpace(imdb_id))
        {
            if (kinopoisk_id > 0)
            {
                string kinopoisk_id_str = kinopoisk_id.ToString();
                foreach (var eid in externalids)
                {
                    if (eid.Value == kinopoisk_id_str && !string.IsNullOrEmpty(eid.Key))
                    {
                        imdb_id = eid.Key;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(imdb_id) && long.TryParse(id, out long _testid) && _testid > 0)
            {
                await using (var sqlDb = ExternalidsContext.Factory != null
                    ? ExternalidsContext.Factory.CreateDbContext()
                    : new ExternalidsContext())
                {
                    imdb_id = sqlDb.imdb.Find($"{id}_{serial}")?.value;
                }

                if (string.IsNullOrEmpty(imdb_id))
                {
                    string mkey = $"externalids:locktmdb:{serial}:{id}";
                    if (!memoryCache.TryGetValue(mkey, out _))
                    {
                        memoryCache.Set(mkey, 0, DateTime.Now.AddHours(1));

                        string cat = serial == 1 ? "tv" : "movie";
                        var header = HeadersModel.Init(("lcrqpasswd", CoreInit.rootPasswd));
                        string json = await Http.Get($"http://api.themoviedb.org/3/{cat}/{id}?api_key={CoreInit.conf.cub.api_key}&append_to_response=external_ids", timeoutSeconds: 5, headers: header);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            imdb_id = Regex.Match(json, "\"imdb_id\":\"(tt[0-9]+)\"").Groups[1].Value;
                            if (!string.IsNullOrEmpty(imdb_id))
                            {
                                await using (var sqlDb = ExternalidsContext.Factory != null
                                    ? ExternalidsContext.Factory.CreateDbContext()
                                    : new ExternalidsContext())
                                {
                                    sqlDb.Add(new ExternalidsSqlModel()
                                    {
                                        Id = $"{id}_{serial}",
                                        value = imdb_id
                                    });

                                    await sqlDb.SaveChangesLocks();
                                }
                            }
                        }
                    }
                }
            }
        }
        #endregion

        #region get kinopoisk_id
        string kpid = null;

        if (!string.IsNullOrWhiteSpace(imdb_id))
        {
            externalids.TryGetValue(imdb_id, out kpid);

            if (string.IsNullOrEmpty(kpid) || kpid == "0")
            {
                await using (var sqlDb = ExternalidsContext.Factory != null
                    ? ExternalidsContext.Factory.CreateDbContext()
                    : new ExternalidsContext())
                {
                    kpid = sqlDb.kinopoisk.Find(imdb_id)?.value;

                    if (string.IsNullOrEmpty(kpid) && kinopoisk_id == 0)
                    {
                        string mkey = $"externalids:lockkpid:{imdb_id}";
                        if (!memoryCache.TryGetValue(mkey, out _))
                        {
                            memoryCache.Set(mkey, 0, DateTime.Now.AddDays(1));

                            switch (ModInit.conf.findkp ?? "all")
                            {
                                case "alloha":
                                    kpid = await getAlloha(imdb_id);
                                    break;
                                //case "vsdn":
                                //    kpid = await getVSDN(imdb_id);
                                //    break;
                                case "tabus":
                                    kpid = await getTabus(imdb_id);
                                    break;
                                default:
                                    {
                                        var tasks = new List<Task<string>> { /*getVSDN(imdb_id),*/ getAlloha(imdb_id), getTabus(imdb_id) };

                                        while (tasks.Count > 0)
                                        {
                                            var completedTask = await Task.WhenAny(tasks);
                                            tasks.Remove(completedTask);

                                            var result = completedTask.Result;
                                            if (result != null)
                                            {
                                                kpid = result;
                                                break;
                                            }
                                        }

                                        break;
                                    }
                            }

                            if (!string.IsNullOrEmpty(kpid) && kpid != "0")
                            {
                                sqlDb.Add(new ExternalidsSqlModel()
                                {
                                    Id = imdb_id,
                                    value = kpid
                                });

                                await sqlDb.SaveChangesLocks();
                            }
                        }
                    }
                }
            }
        }
        #endregion

        kpid = kpid != null ? kpid : kinopoisk_id.ToString();

        if (EventListener.Externalids != null)
        {
            foreach (Func<EventExternalids, (string imdb_id, string kinopoisk_id)> handler in EventListener.Externalids.GetInvocationList())
            {
                var result = handler(new EventExternalids(id, imdb_id, kpid, serial));

                if (string.IsNullOrWhiteSpace(imdb_id) && !string.IsNullOrWhiteSpace(result.imdb_id))
                    imdb_id = result.imdb_id;

                if ((string.IsNullOrWhiteSpace(kpid) || kpid == "0") && !string.IsNullOrWhiteSpace(result.kinopoisk_id) && result.kinopoisk_id != "0")
                    kpid = result.kinopoisk_id;
            }
        }

        jsonResult = $"{{\"imdb_id\":\"{imdb_id}\",\"kinopoisk_id\":\"{kpid}\"}}";
        memoryCache.Set(memKey, jsonResult, DateTime.Now.AddHours(1));

        return Content(jsonResult, "application/json; charset=utf-8");
    }
    #endregion

    #region WithSearch
    [HttpGet]
    [AllowAnonymous]
    [Route("lite/withsearch")]
    public ActionResult WithSearch()
    {
        if (CoreInit.conf.online.with_search == null)
            return ContentTo("[]");

        return Json(CoreInit.conf.online.with_search);
    }
    #endregion

    #region spider
    [HttpGet]
    [Route("lite/spider")]
    [Route("lite/spider/anime")]
    async public Task<ActionResult> Spider(string title)
    {
        if (!ModInit.conf.spider)
            return ContentTo("{}");

        var rch = new RchClient(HttpContext, host, new BaseSettings() { rhub = true }, requestInfo);
        if (rch.IsNotConnected() || rch.IsRequiredConnected())
            return ContentTo(rch.connectionMsg);

        var user = requestInfo.user;
        var piders = new List<(string name, string uri, int index)>();

        bool isanime = HttpContext.Request.Path.Value?.EndsWith("/anime") == true;

        #region send
        void send(BaseSettings init, string plugin = null)
        {
            if (init == null || !init.spider || !init.enable || init.rip)
                return;

            if (init.geo_hide != null)
            {
                if (requestInfo.Country != null && init.geo_hide.Contains(requestInfo.Country))
                    return;
            }

            if (init.group_hide)
            {
                if (init.group > 0)
                {
                    if (user == null || init.group > user.group)
                        return;
                }
                else if (CoreInit.conf.accsdb.enable)
                {
                    if (user == null)
                        return;
                }
            }

            string url = null;
            string displayname = init.displayname ?? init.plugin;

            if (string.IsNullOrEmpty(init.overridepasswd))
            {
                url = init.overridehost;
                if (string.IsNullOrEmpty(url) && init.overridehosts != null && init.overridehosts.Length > 0)
                    url = init.overridehosts[Random.Shared.Next(0, init.overridehosts.Length)];
            }

            if (string.IsNullOrEmpty(url))
                url = $"{host}/lite/" + (plugin ?? init.plugin).ToLower();

            piders.Add((init.displayname ?? init.plugin, $"{url}?title={HttpUtility.UrlEncode(title)}&clarification=1&rjson=true&similar=true", init.displayindex));
        }
        #endregion

        #region module
        OnlineModuleEntry.EnsureCache();
        var spiderArgs = new OnlineSpiderModel(title, isanime);

        if (OnlineModuleEntry.Spiders != null && OnlineModuleEntry.Spiders.Count > 0)
        {
            foreach (var entry in OnlineModuleEntry.Spiders)
            {
                try
                {
                    var result = entry.Spider(HttpContext, requestInfo, host, spiderArgs);
                    if (result == null || result.Count == 0)
                        continue;

                    foreach (var item in result)
                        send(item.init, item.plugin);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "CatchId={CatchId}", "id_bd1de14c");
                }
            }
        }

        if (OnlineModuleEntry.SpidersAsync != null && OnlineModuleEntry.SpidersAsync.Count > 0)
        {
            foreach (var entry in OnlineModuleEntry.SpidersAsync)
            {
                try
                {
                    var result = await entry.SpiderAsync(HttpContext, requestInfo, host, spiderArgs);
                    if (result == null || result.Count == 0)
                        continue;

                    foreach (var item in result)
                        send(item.init, item.plugin);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "CatchId={CatchId}", "id_tne6mp1q");
                }
            }
        }
        #endregion

        return Json(piders.OrderByDescending(i => i.index).ToDictionary(k => k.name, v => v.uri));
    }
    #endregion


    #region events
    [HttpGet]
    [AllowAnonymous]
    [Route("lifeevents")]
    public ActionResult LifeEvents(string memkey, long id, string imdb_id, long kinopoisk_id, int serial, long tmdb_id = 0)
    {
        string json = null;
        JsonResult error(string msg) => Json(new { accsdb = true, ready = true, online = new string[] { }, msg });

        if (memoryCache.TryGetValue(memkey, out List<EventLinkItem> links) && links != null)
        {
            int readyCount = links.Count(i => i?.code != null);
            if (readyCount > 0)
            {
                bool ready = links.Count == readyCount;
                var bestConf = CoreInit.conf.online?.bestBalanser;
                bool hideBroken = ready && bestConf != null && bestConf.enable && bestConf.hideBroken;

                // Подтянуть готовый speed-probe (свой или чужого инстанса через PG):
                // links в memoryCache обновляется фоновым ProbeEventsAfterAsync, но при
                // ответе из общего events-кеша фон на этом инстансе не запускался —
                // Peek закрывает и этот случай.
                Dictionary<string, BalanserHealth> probeHealths = null;
                if (bestConf != null && bestConf.enable)
                {
                    string probeKey = BestBalanserService.BuildKey(imdb_id, kinopoisk_id, ProbeTmdbKey(tmdb_id, id.ToString()), serial);
                    probeHealths = BestBalanserService.Peek(probeKey);
                    if (probeHealths != null)
                        ApplyHealths(links, probeHealths);
                }

                var visible = links.Where(i => i?.code != null);
                if (hideBroken)
                    visible = visible.Where(i => i.work);

                // Сначала подтверждённые пробой (speedScore > 0, реально играют), внутри —
                // по замеренному finalScore; неподтверждённые ниже, по заявленному качеству.
                string online = string.Join(",", visible
                    .OrderByDescending(i => i.work)
                    .ThenByDescending(i => i.speedScore > 0)
                    .ThenByDescending(i => i.speedScore)
                    .ThenByDescending(i => i.qualityScore)
                    .ThenBy(i => i.index)
                    .Select(i => i.code));

                if (ready && !online.Contains("\"show\":true"))
                {
                    if (string.IsNullOrEmpty(imdb_id) && 0 >= kinopoisk_id)
                        return error($"Добавьте \"IMDB ID\" {(serial == 1 ? "сериала" : "фильма")} на https://themoviedb.org/{(serial == 1 ? "tv" : "movie")}/{id}/edit?active_nav_item=external_ids");

                    return error($"Не удалось найти онлайн для {(serial == 1 ? "сериала" : "фильма")}");
                }

                // probe: карта plugin → результат замера. Плагин её игнорирует, но наш
                // пробер и фронт по ней отличают «подтверждён пробой» от «просто нашёлся».
                string probeJson = "";
                if (probeHealths != null && probeHealths.Count > 0)
                {
                    probeJson = ",\"probe\":{" + string.Join(",", probeHealths.Values
                        .Where(h => h != null && !string.IsNullOrEmpty(h.plugin))
                        .Select(h => $"\"{h.plugin.ToLower()}\":{{\"ok\":{h.isWorking.ToString().ToLower()},\"score\":{h.finalScore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"quality\":\"{h.quality}\"}}")) + "}";
                }

                json = $"{{\"ready\":{(ready ? "true" : "false")},\"tasks\":{links.Count}{probeJson},\"online\":[{online.Replace("{localhost}", host)}]}}";
            }
        }

        return ContentTo(json ?? "{\"ready\":false,\"tasks\":0,\"online\":[]}");
    }


    static readonly Regex chineseRegex = new Regex("[\u4E00-\u9FFF]"); // Диапазон для китайских иероглифов
    static readonly Regex japaneseRegex = new Regex("[\u3040-\u30FF\uFF66-\uFF9F]"); // Хирагана, катакана и специальные символы
    static readonly Regex koreanRegex = new Regex("[\uAC00-\uD7AF]"); // Диапазон для корейских хангыльских символов

    [HttpGet]
    [Route("lite/events")]
    async public Task<ActionResult> Events(string id, string imdb_id, long kinopoisk_id, long tmdb_id, string title, string original_title, string original_language, int year, string source, string rchtype, int serial = -1, bool life = false, bool islite = false, string account_email = null, string uid = null, string token = null, string nws_id = null)
    {
        var online = new List<(string name, string url, string plugin, int index)>(50);
        bool isanime = original_language is "ja" or "zh";

        #region fix title
        bool fix_title = false;

        if (title != null && original_language != null && original_language.Split("|")[0] is "ja" or "ko" or "zh" or "cn")
        {
            if (long.TryParse(id, out long tmdbid) && tmdbid > 0)
            {
                if (chineseRegex.IsMatch(title) || japaneseRegex.IsMatch(title) || koreanRegex.IsMatch(title))
                {
                    string memkey = $"themoviedb:fix_title:{serial}:{tmdbid}";
                    if (!memoryCache.TryGetValue(memkey, out string engName))
                    {
                        var header = HeadersModel.Init(("lcrqpasswd", CoreInit.rootPasswd));
                        var result = await Http.Get<JObject>($"http://api.themoviedb.org/3/{(serial == 1 ? "tv" : "movie")}/{tmdbid}?api_key={CoreInit.conf.cub.api_key}&language=en", timeoutSeconds: 4, headers: header);
                        if (result != null)
                            engName = serial == 1 ? result.Value<string>("name") : result.Value<string>("title");

                        memoryCache.Set(memkey, engName ?? string.Empty, DateTime.Now.AddDays(1));
                    }

                    if (!string.IsNullOrEmpty(engName))
                    {
                        title = engName;
                        fix_title = true;
                    }
                }
            }
        }
        #endregion

        var user = requestInfo.user;
        JObject kitconf = loadKitConf();

        #region send
        void send(BaseSettings _init, string plugin = null, string name = null, string arg_title = null, string arg_url = null, string myurl = null)
        {
            var init = loadKit(_init, kitconf);

            if (rchtype != null)
            {
                if (init.client_type != null && !init.client_type.Contains(rchtype))
                    return;

                string rch_deny = init.RchAccessNotSupport();
                if (rch_deny != null && rch_deny.Contains(rchtype))
                    return;

                string stream_deny = init.StreamAccessNotSupport();
                if (stream_deny != null && stream_deny.Contains(rchtype))
                    return;

                if (init.rhub && !init.rhub_fallback && !init.corseu && string.IsNullOrWhiteSpace(init.webcorshost))
                {
                    if (init.rhub_geo_disable != null &&
                        requestInfo.Country != null &&
                        init.rhub_geo_disable.Contains(requestInfo.Country))
                    {
                        return;
                    }
                }
            }

            if (init.geo_hide != null &&
                requestInfo.Country != null &&
                init.geo_hide.Contains(requestInfo.Country))
            {
                return;
            }

            if (init.group_hide)
            {
                if (init.group > 0)
                {
                    if (user == null || init.group > user.group)
                        return;
                }
                else if (CoreInit.conf.accsdb.enable)
                {
                    if (user == null)
                        return;
                }
            }

            string url = string.Empty;

            if (string.IsNullOrEmpty(init.overridepasswd))
            {
                url = init.overridehost;
                if (string.IsNullOrEmpty(url) && init.overridehosts != null && init.overridehosts.Length > 0)
                    url = init.overridehosts[Random.Shared.Next(0, init.overridehosts.Length)];
            }

            bool enable = init.enable && !init.rip;
            if (!enable && string.IsNullOrEmpty(url))
                return;

            string displayname = init.displayname ?? name ?? init.plugin;

            if (string.IsNullOrEmpty(url))
            {
                url = !string.IsNullOrEmpty(myurl)
                    ? url = myurl + arg_url
                    : url = "{localhost}/lite/" + (plugin ?? (init.plugin ?? name).ToLower()) + arg_url;
            }

            if (original_language != null && original_language.Split("|")[0] is "ru" or "ja" or "ko" or "zh" or "cn")
            {
                string _p = (plugin ?? (init.plugin ?? name).ToLower());
                if (_p is "filmix" or "filmixtv" or "fxapi" or "kinoukr" or "rezka" or "rhsprem" or "kinopub" or "alloha" or "fancdn" or "kinotochka" or "remux" or "kinogo" or "kinobase" or "getstv" or "leproduction") // || (_p == "kodik" && kinopoisk_id == 0 && string.IsNullOrEmpty(imdb_id))
                    url += (url.Contains("?") ? "&" : "?") + "clarification=1";
            }

            online.Add(($"{displayname}{arg_title}", url, (plugin ?? init.plugin ?? name).ToLower(), init.displayindex > 0 ? init.displayindex : online.Count));
        }
        #endregion

        #region modules
        OnlineModuleEntry.EnsureCache();
        var moduleArgs = new OnlineEventsModel(id, imdb_id, kinopoisk_id, title, original_title, original_language, year, source, rchtype, serial, isanime, life, islite, account_email, uid, token, nws_id, kitconf);

        if (OnlineModuleEntry.Modules != null && OnlineModuleEntry.Modules.Count > 0)
        {
            foreach (var entry in OnlineModuleEntry.Modules)
            {
                try
                {
                    var result = entry.Invoke(HttpContext, requestInfo, host, moduleArgs);
                    if (result != null && result.Count > 0)
                    {
                        foreach (var r in result)
                            send(r.init, r.plugin, r.name, r.arg_title, r.arg_url, r.myurl);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "CatchId={CatchId}", "id_a73m6i2y");
                }
            }
        }

        if (OnlineModuleEntry.ModulesAsync != null && OnlineModuleEntry.ModulesAsync.Count > 0)
        {
            foreach (var entry in OnlineModuleEntry.ModulesAsync)
            {
                try
                {
                    var result = await entry.InvokeAsync(HttpContext, requestInfo, host, moduleArgs);
                    if (result != null && result.Count > 0)
                    {
                        foreach (var r in result)
                            send(r.init, r.plugin, r.name, r.arg_title, r.arg_url, r.myurl);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "CatchId={CatchId}", "id_xnfe4pvc");
                }
            }
        }
        #endregion

        #region EventListener
        if (EventListener.OnlineChannels != null)
        {
            var em = new EventOnline(this, online, moduleArgs, kitconf, HttpContext);
            foreach (Func<EventOnline, ActionResult> handler in EventListener.OnlineChannels.GetInvocationList())
            {
                var eventResult = handler(em);
                if (eventResult != null)
                    return eventResult;
            }
        }
        #endregion

        #region checkOnlineSearch
        if (ModInit.conf.checkOnlineSearch && !string.IsNullOrEmpty(id))
        {
            string memkey = CrypTo.md5($"checkOnlineSearch:{id}:{serial}:{source?.Replace("tmdb", "")?.Replace("cub", "")}:{online.Count}:{(IsKitConf ? requestInfo.user_uid : null)}");

            // Общий для всех инстансов ключ events-кеша в PG: один фильм — один результат,
            // независимо от инстанса и uid. При kitconf-персонализации PG-шаринг отключён.
            bool pgEvents = !IsKitConf && EventsPgMode;
            string eventsKey = pgEvents
                ? $"events:{id}:{serial}:{source?.Replace("tmdb", "")?.Replace("cub", "")}"
                : null;

            if (!memoryCache.TryGetValue(memkey, out List<EventLinkItem> links))
            {
                // Общий результат /lite/events из PG — собран любым инстансом, отдаём без фан-аута.
                links = pgEvents ? await ReadEventsPg(eventsKey) : null;
                bool fromPgEvents = links != null;

                var tasks = new List<Task>();
                var errflag = new int[1];

                if (fromPgEvents)
                {
                    memoryCache.Set(memkey, links, DateTime.Now.AddMinutes(5));
                }
                else
                {
                    links = new List<EventLinkItem>(online.Count);
                    for (int i = 0; i < online.Count; i++)
                        links.Add(default);

                    memoryCache.Set(memkey, links, DateTime.Now.AddMinutes(5));

                    foreach (var o in online.OrderBy(i => i.index))
                    {
                        var tk = checkSearch(memkey, kitconf, links, tasks.Count, o.index, o.name, o.url, o.plugin, id, imdb_id, kinopoisk_id, tmdb_id, title, original_title, original_language, source, year, serial, life, rchtype, errflag);
                        tasks.Add(tk);
                    }
                }

                if (life)
                {
                    if (!fromPgEvents && pgEvents)
                        _ = WriteEventsAfter(tasks, eventsKey, links, errflag);

                    #region speed-probe в life-режиме
                    // Продовый плагин ходит ТОЛЬКО с life=true, поэтому probe обязан
                    // стартовать и здесь: дожидаемся checkSearch-тасков в фоне, меряем
                    // рабочие балансеры и дописываем ранжирование в links (тот же объект
                    // лежит в memoryCache[memkey] — lifeevents подхватит на очередном
                    // поллинге через Peek/ApplyHealths).
                    var lifeBestConf = CoreInit.conf.online?.bestBalanser;
                    if (lifeBestConf != null && lifeBestConf.enable)
                    {
                        var lifeLoopbackBase = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}";
                        var lifeLoopbackHeaders = new Dictionary<string, string>
                        {
                            ["xhost"] = host,
                            ["xscheme"] = HttpContext.Request.Scheme,
                            ["lcrqpasswd"] = CoreInit.rootPasswd
                        };

                        string lifeBaseQuery = $"id={HttpUtility.UrlEncode(id)}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&tmdb_id={tmdb_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&original_language={original_language}&source={source}&year={year}&serial={serial}&rchtype={rchtype}";
                        string lifeProbeKey = BestBalanserService.BuildKey(imdb_id, kinopoisk_id, ProbeTmdbKey(tmdb_id, id), serial);

                        _ = ProbeEventsAfterAsync(tasks, links, online, lifeProbeKey, lifeLoopbackBase, lifeLoopbackHeaders,
                            lifeBaseQuery, serial == 1, lifeBestConf, eventsKey, pgEvents);
                    }
                    #endregion

                    return Json(new { life = true, memkey, title = (fix_title ? title : null) });
                }

                if (!fromPgEvents)
                {
                await Task.WhenAll(tasks);

                #region speed-probe — фоновое ранжирование рабочих балансеров
                Task<Dictionary<string, BalanserHealth>> probeBackground = null;
                var bestConf = CoreInit.conf.online?.bestBalanser;
                if (bestConf != null && bestConf.enable)
                {
                    var loopbackBase = $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}";
                    var loopbackHeaders = new Dictionary<string, string>
                    {
                        ["xhost"] = host,
                        ["xscheme"] = HttpContext.Request.Scheme,
                        ["lcrqpasswd"] = CoreInit.rootPasswd
                    };

                    string baseQuery = $"id={HttpUtility.UrlEncode(id)}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&tmdb_id={tmdb_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&original_language={original_language}&source={source}&year={year}&serial={serial}&rchtype={rchtype}";

                    var candidates = new List<BalanserCandidate>(links.Count);
                    for (int i = 0; i < links.Count; i++)
                    {
                        var l = links[i];
                        if (l == null || !l.work || string.IsNullOrEmpty(l.plugin)) continue;

                        // Восстановить URL: из l.code (JSON) уже не достать удобно, поэтому идём по online
                        var o = online.FirstOrDefault(x => string.Equals(x.plugin, l.plugin, StringComparison.OrdinalIgnoreCase));
                        if (string.IsNullOrEmpty(o.url)) continue;

                        string fullUrl = o.url.Replace("{localhost}", loopbackBase)
                            + (o.url.Contains("?") ? "&" : "?") + baseQuery;

                        candidates.Add(new BalanserCandidate(l.plugin, o.name, fullUrl));
                    }

                    string probeKey = BestBalanserService.BuildKey(imdb_id, kinopoisk_id, ProbeTmdbKey(tmdb_id, id), serial);

                    // Готовый замер из локального кеша — применяем сразу, без задержки.
                    var cachedHealths = BestBalanserService.Peek(probeKey);
                    if (cachedHealths != null)
                    {
                        ApplyHealths(links, cachedHealths);
                    }
                    else if (candidates.Count > 0)
                    {
                        // Замера ещё нет — гоняем probe В ФОНЕ (CancellationToken.None, чтобы он
                        // пережил завершение запроса). Текущий запрос отдаётся без ранжирования,
                        // результат подхватят следующие запросы из balanser_best / events_cache.
                        probeBackground = BestBalanserService.RunOrJoinAsync(
                            probeKey, candidates, isSerial: serial == 1, loopbackHeaders,
                            bestConf.totalTimeoutMs, bestConf.perProbeTimeoutMs,
                            bestConf.speedSamples, bestConf.maxRetries,
                            bestConf.successCacheMinutes, bestConf.failureCacheMinutes,
                            CancellationToken.None);
                    }
                }
                #endregion

                // Партиал с упавшим под-запросом не морозим на 5 мин — кешируем кратко (60с),
                // чтобы он быстро самовылечился. Чистая сборка живёт полные 5 минут.
                bool unreliable = errflag[0] > 0 || links.Any(l => l == null);
                var eventsTtl = unreliable ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);
                memoryCache.Set(memkey, links, DateTime.Now.Add(eventsTtl));

                if (pgEvents)
                {
                    // merge-запись: общий результат обновится, только если он не беднее.
                    // RETURNING отдаёт актуальную версию — union от всех инстансов.
                    var merged = await WriteEventsPg(eventsKey, links, eventsTtl);
                    if (merged != null && merged.Count > 0)
                    {
                        links = merged;
                        memoryCache.Set(memkey, links, DateTime.Now.Add(eventsTtl));
                    }
                }

                // Фоновый probe: по завершении дописываем ранжирование в links (тот же объект
                // лежит в memoryCache[memkey]) и переписываем общий events_cache в PG.
                if (probeBackground != null)
                {
                    var bgLinks = links;
                    _ = probeBackground.ContinueWith(t =>
                    {
                        if (t.Status != TaskStatus.RanToCompletion || t.Result == null)
                            return;
                        ApplyHealths(bgLinks, t.Result);
                        if (pgEvents)
                            _ = WriteEventsPg(eventsKey, bgLinks, eventsTtl);
                    }, TaskScheduler.Default);
                }
                }
            }

            if (life)
                return Json(new { life = true, memkey });

            var bestConf2 = CoreInit.conf.online?.bestBalanser;
            bool hideBroken = bestConf2 != null && bestConf2.enable && bestConf2.hideBroken;

            var visible = links.Where(i => i?.code != null);
            if (hideBroken)
                visible = visible.Where(i => i.work);

            var sorted = visible
                .OrderByDescending(i => i.work)
                .ThenByDescending(i => i.speedScore > 0)
                .ThenBy(i => i.deprioritize)
                .ThenByDescending(i => i.speedScore)
                .ThenByDescending(i => i.qualityScore)
                .ThenBy(i => i.index)
                .Select(i => i.code);

            return ContentTo($"[{string.Join(",", sorted).Replace("{localhost}", host)}]");
        }
        #endregion

        string online_result = string.Join(",", online.OrderBy(i => i.index).Select(i => "{\"name\":\"" + i.name + "\",\"url\":\"" + i.url + "\",\"balanser\":\"" + i.plugin + "\"}"));
        return ContentTo($"[{online_result.Replace("{localhost}", host)}]");
    }
    #endregion

    #region checkSearch
    async Task checkSearch(string memkey, JObject kitconf, List<EventLinkItem> links, int indexList, int index, string name, string uri, string plugin,
                           string id, string imdb_id, long kinopoisk_id, long tmdb_id, string title, string original_title, string original_language, string source, int year, int serial, bool life, string rchtype, int[] errflag)
    {
        try
        {
            string srq = uri.Replace("{localhost}", $"http://{CoreInit.conf.listen.localhost}:{CoreInit.conf.listen.port}");
            var header = uri.Contains("{localhost}") ? HeadersModel.Init(("xhost", host), ("xscheme", HttpContext.Request.Scheme), ("lcrqpasswd", CoreInit.rootPasswd)) : null;

            string checkuri = $"{srq}{(srq.Contains("?") ? "&" : "?")}id={HttpUtility.UrlEncode(id)}&imdb_id={imdb_id}&kinopoisk_id={kinopoisk_id}&tmdb_id={tmdb_id}&title={HttpUtility.UrlEncode(title)}&original_title={HttpUtility.UrlEncode(original_title)}&original_language={original_language}&source={source}&year={year}&serial={serial}&rchtype={rchtype}&checksearch=true";
            string res = await Http.Get(AccsDbInvk.Args(checkuri, HttpContext), timeoutSeconds: 10, headers: header);

            if (string.IsNullOrEmpty(res))
            {
                // null от Http.Get = таймаут / non-200 / пустой ответ → под-запрос упал.
                System.Threading.Interlocked.Increment(ref errflag[0]);
                res = string.Empty;
            }

            bool rch = res.Contains("\"rch\":true");
            bool work = rch || res.Contains("data-json=")
                || res.Contains("\"type\":\"movie\"")
                || res.Contains("\"type\":\"episode\"")
                || res.Contains("\"type\":\"season\"");

            string quality = string.Empty;
            string balanser = plugin.Contains("/") ? plugin.Split("/")[1] : plugin;

            // Извлекаем quality всегда когда есть work — чтобы bestBalanser мог отсортировать.
            bool extractQuality = work && (life || CoreInit.conf.online?.bestBalanser?.enable == true);

            #region определение качества
            if (extractQuality)
            {
                foreach (string q in new string[] { "2160", "1080", "720", "480", "360" })
                {
                    if (res.Contains("<!--q:"))
                    {
                        quality = " - " + Regex.Match(res, "<!--q:([^>]+)-->").Groups[1].Value;
                        break;
                    }
                    else if (res.Contains($"\"{q}p\"") || res.Contains($">{q}p<") || res.Contains($"<!--{q}p-->"))
                    {
                        quality = $" - {q}p";
                        break;
                    }
                }

                if (quality == "2160")
                    quality = res.Contains("HDR") ? " - 4K HDR" : " - 4K";

                if (quality == string.Empty)
                {
                    if (EventListener.OnlineApiQuality != null)
                    {
                        var em = new EventOnlineApiQuality(balanser, kitconf);
                        foreach (Func<EventOnlineApiQuality, string> handler in EventListener.OnlineApiQuality.GetInvocationList())
                        {
                            string eventQuality = handler.Invoke(em);
                            if (eventQuality != null)
                            {
                                quality = eventQuality;
                                break;
                            }
                        }
                    }

                    if (balanser == "vokino")
                        quality = res.Contains("4K HDR") ? " - 4K HDR" : res.Contains("4K ") ? " - 4K" : quality;
                }
            }
            #endregion

            if (!name.Contains(" - ") && ModInit.conf.showquality && !string.IsNullOrEmpty(quality))
            {
                name = Regex.Replace(name, " ~ .*$", "");
                name += quality;
            }

            int qualityScore = ParseQualityScore(quality);
            bool deprioritize = name.Contains("(Украинский)");

            links[indexList] = new("{" + $"\"name\":\"{name}\",\"url\":\"{uri}\",\"index\":{index},\"show\":{work.ToString().ToLower()},\"balanser\":\"{plugin}\",\"rch\":{rch.ToString().ToLower()}" + "}", index, work, plugin, qualityScore, 0, deprioritize);
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Increment(ref errflag[0]);
            Serilog.Log.Error(ex, "CatchId={CatchId}", "id_effc21fb");
        }
    }

    static int ParseQualityScore(string label)
    {
        if (string.IsNullOrEmpty(label)) return 0;
        if (label.Contains("4K", StringComparison.OrdinalIgnoreCase)) return 90;
        var m = Regex.Match(label, @"(\d{3,4})");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n)) return 0;
        // Нестандартные высоты вроде 1036p/690p раскладываются в стандартные тиры:
        // 2160+ → 4K, 1440+ → 1440p, 1024+ → 1080p, 700+ → 720p и т.д.
        if (n >= 2160) return 90;
        if (n >= 1440) return 70;
        if (n >= 1024) return 60;
        if (n >= 700)  return 40;
        if (n >= 460)  return 20;
        if (n >= 340)  return 10;
        return 0;
    }

    static readonly Regex _codeNameRx = new("\"name\":\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly Regex _nameTailQualityRx = new(@"\s*[-~]\s*(?:\d{3,4}p?|4K(?:\s*HDR)?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static string NormalizeQualityLabel(string q)
    {
        if (string.IsNullOrEmpty(q)) return q;
        var m = Regex.Match(q, @"(\d{3,4})");
        if (!m.Success) return q;
        if (!int.TryParse(m.Groups[1].Value, out int n)) return q;
        if (n >= 2160) return q.Contains("HDR", StringComparison.OrdinalIgnoreCase) ? "4K HDR" : "4K";
        return n + "p";
    }

    static string RewriteCodeNameQuality(string code, string realQuality)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(realQuality)) return code;
        string normalized = NormalizeQualityLabel(realQuality);
        return _codeNameRx.Replace(code, m =>
        {
            string name = _nameTailQualityRx.Replace(m.Groups[1].Value, "").TrimEnd();
            return "\"name\":\"" + name + " ~ " + normalized + "\"";
        }, 1);
    }
    #endregion

    // Применить результат speed-probe к списку источников: speedScore для сортировки +
    // уточнённое качество. Probe НЕ скрывает источники (его резолвер ненадёжен) — вердикт
    // «источник есть» остаётся за checkSearch, probe может работу только подтвердить.
    static void ApplyHealths(List<EventLinkItem> links, Dictionary<string, BalanserHealth> healths)
    {
        if (links == null || healths == null || healths.Count == 0)
            return;

        for (int i = 0; i < links.Count; i++)
        {
            var l = links[i];
            if (l == null || string.IsNullOrEmpty(l.plugin)) continue;
            if (!healths.TryGetValue(l.plugin, out var h) || h == null) continue;

            bool newWork = h.isWorking || h.isRch || l.work;
            int probeQ = ParseQualityScore(h.quality);
            int newQ = probeQ > 0 ? probeQ : l.qualityScore;
            string newCode = h.isWorking && !string.IsNullOrEmpty(h.quality)
                ? RewriteCodeNameQuality(l.code, h.quality)
                : l.code;
            links[i] = l with { code = newCode, speedScore = h.finalScore, work = newWork, qualityScore = newQ };
        }
    }

    #region events_cache (общий кеш агрегации /lite/events)
    static bool EventsPgMode =>
        Shared.CoreInit.conf.cache?.type == "pg" && Shared.Services.Hybrid.PostgresHybridCache.DataSource != null;

    // Считать общий результат /lite/events из PG (собран любым инстансом).
    static async Task<List<EventLinkItem>> ReadEventsPg(string movieKey)
    {
        try
        {
            string payload = await Shared.Services.Hybrid.PostgresHybridCache.ReadEventsAsync(movieKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(payload))
                return null;

            var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<EventLinkItem>>(payload);
            return list != null && list.Count > 0 ? list : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "CatchId={CatchId}", "events_pg_read");
            return null;
        }
    }

    // Записать собранный результат в общий PG-кеш (merge) и вернуть актуальный список.
    static async Task<List<EventLinkItem>> WriteEventsPg(string movieKey, List<EventLinkItem> links, TimeSpan ttl)
    {
        try
        {
            var clean = links.Where(l => l != null && l.code != null).ToList();
            if (clean.Count == 0)
                return null;

            int workCount = clean.Count(l => l.work);
            string payload = Newtonsoft.Json.JsonConvert.SerializeObject(clean);
            string merged = await Shared.Services.Hybrid.PostgresHybridCache.WriteEventsMergeAsync(
                movieKey, payload, workCount, DateTimeOffset.Now.Add(ttl)).ConfigureAwait(false);

            if (string.IsNullOrEmpty(merged))
                return null;

            return Newtonsoft.Json.JsonConvert.DeserializeObject<List<EventLinkItem>>(merged);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "CatchId={CatchId}", "events_pg_write");
            return null;
        }
    }

    // life-режим: дождаться фоновых checkSearch-тасков и записать результат в общий PG-кеш.
    static async Task WriteEventsAfter(List<Task> tasks, string movieKey, List<EventLinkItem> links, int[] errflag)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch { }

        try
        {
            bool unreliable = errflag[0] > 0 || links.Any(l => l == null);
            await WriteEventsPg(movieKey, links, unreliable ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch { }
    }

    // tmdb-компонент ключа пробы: у большинства карточек tmdb_id не приходит отдельным
    // параметром, но id и есть tmdb id (source=tmdb). Без фолбэка карточки без
    // imdb/kp/tmdb слипались бы в один ключ "…|0|0|serial" и делили чужие замеры.
    static long ProbeTmdbKey(long tmdb_id, string id)
    {
        if (tmdb_id > 0) return tmdb_id;
        return long.TryParse(id, out long fromId) && fromId > 0 ? fromId : 0;
    }

    // life-режим: дождаться checkSearch-тасков, прогнать speed-probe по найденным
    // балансерам и вписать ранжирование в links (объект живёт в memoryCache — его
    // читает каждый поллинг lifeevents). Затем обновить общий events-кеш в PG.
    static async Task ProbeEventsAfterAsync(
        List<Task> tasks,
        List<EventLinkItem> links,
        List<(string name, string url, string plugin, int index)> online,
        string probeKey,
        string loopbackBase,
        Dictionary<string, string> loopbackHeaders,
        string baseQuery,
        bool isSerial,
        Shared.Models.AppConf.BestBalanserConf bestConf,
        string eventsKey,
        bool pgEvents)
    {
        try
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch { }

            // Готовый замер (свой или чужой из PG) — применяем без повторной пробы.
            var cached = BestBalanserService.Peek(probeKey);
            if (cached != null)
            {
                ApplyHealths(links, cached);
                return;
            }

            var candidates = new List<BalanserCandidate>(links.Count);
            foreach (var l in links)
            {
                if (l == null || !l.work || string.IsNullOrEmpty(l.plugin)) continue;

                var o = online.FirstOrDefault(x => string.Equals(x.plugin, l.plugin, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(o.url)) continue;

                string fullUrl = o.url.Replace("{localhost}", loopbackBase)
                    + (o.url.Contains("?") ? "&" : "?") + baseQuery;

                candidates.Add(new BalanserCandidate(l.plugin, o.name, fullUrl));
            }

            if (candidates.Count == 0)
                return;

            var healths = await BestBalanserService.RunOrJoinAsync(
                probeKey, candidates, isSerial, loopbackHeaders,
                bestConf.totalTimeoutMs, bestConf.perProbeTimeoutMs,
                bestConf.speedSamples, bestConf.maxRetries,
                bestConf.successCacheMinutes, bestConf.failureCacheMinutes,
                CancellationToken.None).ConfigureAwait(false);

            if (healths == null || healths.Count == 0)
                return;

            ApplyHealths(links, healths);

            if (pgEvents)
                await WriteEventsPg(eventsKey, links, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "CatchId={CatchId}", "probe_life_after");
        }
    }
    #endregion
}
