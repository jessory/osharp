﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using OSharp.Web.Http.Extensions;
using OSharp.Web.Http.Internal;
using OSharp.Web.Http.Messages;


namespace OSharp.Web.Http.Caching
{
    /// <summary>
    /// Represents a message handler that implements caching and supports
    /// (loosely based on Glenn Block's ETagHandler)
    /// * Resource retrieval by ETag
    /// * Resource retrieval by LastModified
    /// * If-Match and If-None-Match for GET operations
    /// * If-Modified-Since and If-Unmodified-Since for GET operations
    /// * If-Unmodified-Since and If-Match for PUT operations
    /// * Will add ETag, LastModified and Vary headers in the response
    /// * Allows caching to be turned off based on individual message
    /// * Currently does not support If-Range headers
    /// </summary>
    public class CachingHandler : DelegatingHandler
    {
        protected readonly IEntityTagStore _entityTagStore;
        private readonly string[] _varyByHeaders;
        private readonly object _padLock = new object();

        /// <summary>
        /// A Chain of responsibility of rules for handling various scenarios. 
        /// List is ordered. First one to return a non-null task will break the chain and 
        /// method will return
        /// </summary>
        protected IDictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> RequestInterceptionRules { get; set; }

        public bool AddLastModifiedHeader { get; set; }

        public bool AddVaryHeader { get; set; }

        public CachingHandler(params string[] varyByHeader)
            : this(new InMemoryEntityTagStore(), varyByHeader)
        { }

        public CachingHandler(IEntityTagStore entityTagStore, params string[] varyByHeaders)
        {
            AddLastModifiedHeader = true;
            AddVaryHeader = true;
            _varyByHeaders = varyByHeaders;
            _entityTagStore = entityTagStore;
            ETagValueGenerator = (resourceUri, headers) =>
                new EntityTagHeaderValue(
                    string.Format("\"{0}\"", Guid.NewGuid().ToString("N").ToLower()),
                    varyByHeaders.Length == 0); // default ETag generation will create weak tags if varyByHeaders has zero items

            EntityTagKeyGenerator = (resourceUri, headers) =>
                new EntityTagKey(resourceUri, headers.SelectMany(h => h.Value));

            LinkedRoutePatternProvider = (uri, method) => new string[0]; // a dummy
            UriTrimmer = (uri) => uri.PathAndQuery;

            CacheControlHeaderProvider = (request) => new CacheControlHeaderValue
            {
                Private = true,
                MustRevalidate = true,
                NoTransform = true,
                MaxAge = TimeSpan.FromDays(7)
            };
        }

        /// <summary>
        /// A function which receives URL of the resource and generates a unique value for ETag
        /// It also receives varyByHeaders request headers.
        /// Default value is a function that generates a guid and URL is ignored and
        /// it generates a weak ETag if no varyByHeaders is passed in
        /// </summary>
        public Func<string, IEnumerable<KeyValuePair<string, IEnumerable<string>>>,
            EntityTagHeaderValue> ETagValueGenerator { get; set; }

        /// <summary>
        /// A function which receives URL of the resource and generates a value for ETag key
        /// It also receives varyByHeaders request headers.
        /// Default value is a function that appends URL and all varyByHeader header values.
        /// This extensibility points allows for selected values from the varyByHeader headers
        /// selected and passed in.
        /// </summary>
        public Func<string, IEnumerable<KeyValuePair<string, IEnumerable<string>>>, EntityTagKey>
            EntityTagKeyGenerator { get; set; }

        /// <summary>
        /// This is a function that decides whether caching for a particular request
        /// is supported.
        /// Function can return null to negate any caching. In this case, responses will not be cached
        /// and ETag header will not be sent.
        /// Alternatively it can return a CacheControlHeaderValue which controls cache lifetime on the client.
        /// By default value is set so that all requests are cachable with expiry of 1 week.
        /// </summary>
        public Func<HttpRequestMessage, CacheControlHeaderValue> CacheControlHeaderProvider { get; set; }

        /// <summary>
        /// This is a function to allow the clients to invalidate the cache
        /// for related URLs.
        /// Current resourceUri and HttpMethod is passed and a list of URLs
        /// is retrieved and cache is invalidated for those URLs.
        /// </summary>
        public Func<string, HttpMethod, IEnumerable<string>> LinkedRoutePatternProvider { get; set; }

        /// <summary>
        /// A function that gets the Uri (normally request) and extracts important bits
        /// for keys. By default it will return Uri.PathAndQuery
        /// </summary>
        public Func<Uri, string> UriTrimmer { get; set; }

        protected void ExecuteCacheInvalidationRules(EntityTagKey entityTagKey,
            HttpRequestMessage request,
            HttpResponseMessage response)
        {
            new[]
            {
                InvalidateCache(entityTagKey, request, response), // general invalidation
                PostInvalidationRule(entityTagKey, request, response)
            }
                .Chain()();
        }

        protected void ExecuteCacheAdditionRules(EntityTagKey entityTagKey,
            HttpRequestMessage request,
            HttpResponseMessage response,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> varyHeaders)
        {
            new[]
            {
                AddCaching(entityTagKey, request, response, varyHeaders), // general caching
            }
                .Chain()();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            EnsureRulesSetup();

            IEnumerable<KeyValuePair<string, IEnumerable<string>>> varyByHeaders = request.Headers.Where(h => _varyByHeaders.Any(
                v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));

            Task<HttpResponseMessage> task = null;

            RequestInterceptionRules.Values.FirstOrDefault(r =>
            {
                task = r(request);
                return task != null;
            });

            if (task == null)
            {
                return base.SendAsync(request, cancellationToken)
                    .ContinueWith(GetCachingContinuation(request));
            }
            else
            {
                return task;
            }
        }

        /// <summary>
        /// This is a scenario where we have a POST to a resource
        /// and it needs to invalidate the cache to that resource
        /// and all its linked URLs
        /// 
        /// For example:
        /// POST /api/cars => invalidate /api/cars
        /// also it might invalidate /api/cars/fastest in which case
        /// /api/cars/fastest must be one of the linked URLs
        /// </summary>
        /// <param name="entityTagKey">entityTagKey</param>
        /// <param name="request">request</param>
        /// <param name="response">response</param>
        /// <returns>returns the function to execute</returns>
        internal Action PostInvalidationRule(
            EntityTagKey entityTagKey,
            HttpRequestMessage request,
            HttpResponseMessage response)
        {
            return () =>
            {
                if (request.Method != HttpMethod.Post)
                {
                    return;
                }

                // if location header is set (for newly created resource), invalidate cache for it
                // this normally should not be necessary as the item is new and should not be in the cache
                // but releasing a non-existent item from cache should not have a big overhead
                if (response.Headers.Location != null)
                {
                    _entityTagStore.RemoveAllByRoutePattern(response.Headers.Location.ToString());
                }
            };
        }

        /// <summary>
        /// Adds caching for GET and PUT if 
        /// cache control provided is not null
        /// With PUT, since cache has been alreay invalidated,
        /// we provide the new ETag (old one has been cleared in invalidation phase)
        /// </summary>
        /// <param name="entityTagKey"></param>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <param name="varyHeaders"></param>
        /// <returns></returns>
        internal Action AddCaching(
            EntityTagKey entityTagKey,
            HttpRequestMessage request,
            HttpResponseMessage response,
            IEnumerable<KeyValuePair<string, IEnumerable<string>>> varyHeaders)
        {
            return
                () =>
                {
                    CacheControlHeaderValue cacheControlHeaderValue = CacheControlHeaderProvider(request);
                    if (cacheControlHeaderValue == null)
                    {
                        return;
                    }

                    TimedEntityTagHeaderValue eTagValue;

                    string uri = UriTrimmer(request.RequestUri);

                    // in case of GET and no ETag
                    // in case of PUT, we should return the new ETag of the resource
                    // NOTE: No need to check if it is in the cache. If it were, it would not get
                    // here
                    if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Put)
                    {
                        // create new ETag only if it does not already exist
                        if (!_entityTagStore.TryGetValue(entityTagKey, out eTagValue))
                        {
                            eTagValue = new TimedEntityTagHeaderValue(ETagValueGenerator(uri, varyHeaders));
                            _entityTagStore.AddOrUpdate(entityTagKey, eTagValue);
                        }

                        // set ETag
                        response.Headers.ETag = eTagValue.ToEntityTagHeaderValue();

                        // set last-modified
                        if (AddLastModifiedHeader && response.Content != null
                            && !response.Content.Headers.Any(x => x.Key.Equals(HttpHeaderNames.LastModified,
                                StringComparison.CurrentCultureIgnoreCase)))
                        {
                            response.Content.Headers.Add(HttpHeaderNames.LastModified, eTagValue.LastModified.ToString("r"));
                        }

                        // set Vary
                        if (AddVaryHeader && _varyByHeaders != null && _varyByHeaders.Length > 0)
                        {
                            response.Headers.Add(HttpHeaderNames.Vary, _varyByHeaders);
                        }

                        response.Headers.TryAddWithoutValidation(HttpHeaderNames.CacheControl, cacheControlHeaderValue.ToString());
                    }
                };
        }

        /// <summary>
        /// This invalidates the resource based on routePattern
        /// for methods POST, PUT and DELETE.
        /// It also removes for all linked URLs
        /// </summary>
        /// <param name="entityTagKey"></param>
        /// <param name="request"></param>
        /// <param name="response"></param>
        /// <returns></returns>
        internal Action InvalidateCache(
            EntityTagKey entityTagKey,
            HttpRequestMessage request,
            HttpResponseMessage response)
        {
            return
                () =>
                {
                    if (!request.Method.Method.IsIn("PUT", "DELETE", "POST"))
                    {
                        return;
                    }

                    string uri = UriTrimmer(request.RequestUri);

                    // remove pattern
                    _entityTagStore.RemoveAllByRoutePattern(entityTagKey.RoutePattern);

                    // remove all related URIs
                    IEnumerable<string> linkedUrls = LinkedRoutePatternProvider(uri, request.Method);
                    foreach (string linkedUrl in linkedUrls)
                    {
                        _entityTagStore.RemoveAllByRoutePattern(linkedUrl);
                    }
                };
        }

        internal Func<Task<HttpResponseMessage>, HttpResponseMessage> GetCachingContinuation(HttpRequestMessage request)
        {
            return task =>
            {
                HttpResponseMessage response = task.Result;
                int statusCode = (int)response.StatusCode;
                if (statusCode >= 300 || statusCode < 200) // only if successful carry on processing
                {
                    return response;
                }

                string uri = UriTrimmer(request.RequestUri);
                IEnumerable<KeyValuePair<string, IEnumerable<string>>> varyHeaders = request.Headers.Where(h =>
                    _varyByHeaders.Any(v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));

                EntityTagKey eTagKey = EntityTagKeyGenerator(uri, varyHeaders);

                ExecuteCacheInvalidationRules(eTagKey, request, response);
                ExecuteCacheAdditionRules(eTagKey, request, response, varyHeaders);

                return response;
            };
        }

        private void EnsureRulesSetup()
        {
            if (RequestInterceptionRules == null)
            {
                lock (_padLock)
                {
                    if (RequestInterceptionRules == null) // double if to prevent race condition
                    {
                        BuildRules();
                    }
                }
            }
        }

        protected virtual void BuildRules()
        {
            RequestInterceptionRules = new Dictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
            RequestInterceptionRules.Add("GetIfMatchNoneMatch", GetIfMatchNoneMatch());
            RequestInterceptionRules.Add("GetIfModifiedUnmodifiedSince", GetIfModifiedUnmodifiedSince());
            RequestInterceptionRules.Add("PutIfMatch", PutIfMatch());
            RequestInterceptionRules.Add("PutIfUnmodifiedSince", PutIfUnmodifiedSince());
        }

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> GetIfMatchNoneMatch()
        {
            return (request) =>
            {
                if (request.Method != HttpMethod.Get)
                {
                    return null;
                }

                ICollection<EntityTagHeaderValue> noneMatchTags = request.Headers.IfNoneMatch;
                ICollection<EntityTagHeaderValue> matchTags = request.Headers.IfMatch;

                if (matchTags.Count == 0 && noneMatchTags.Count == 0)
                {
                    return null; // no etag
                }

                if (matchTags.Count > 0 && noneMatchTags.Count > 0) // both if-match and if-none-match exist
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest).ToTask();
                }

                bool isNoneMatch = noneMatchTags.Count > 0;
                ICollection<EntityTagHeaderValue> etags = isNoneMatch ? noneMatchTags : matchTags;

                string resource = UriTrimmer(request.RequestUri);
                IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers =
                    request.Headers.Where(h => _varyByHeaders.Any(v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));
                EntityTagKey entityTagKey = EntityTagKeyGenerator(resource, headers);
                // compare the Etag with the one in the cache
                // do conditional get.
                TimedEntityTagHeaderValue actualEtag = null;

                bool matchFound = false;
                if (_entityTagStore.TryGetValue(entityTagKey, out actualEtag))
                {
                    if (etags.Any(etag => etag.Tag == actualEtag.Tag))
                    {
                        matchFound = true;
                    }
                }
                return matchFound ^ isNoneMatch ? null : new NotModifiedResponse(actualEtag.ToEntityTagHeaderValue()).ToTask();
            };
        }

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> GetIfModifiedUnmodifiedSince()
        {
            return (request) =>
            {
                if (request.Method != HttpMethod.Get && request.Headers != null)
                {
                    return null;
                }

                DateTimeOffset? ifModifiedSince = request.Headers.IfModifiedSince;
                DateTimeOffset? ifUnmodifiedSince = request.Headers.IfUnmodifiedSince;

                if (ifModifiedSince == null && ifUnmodifiedSince == null)
                {
                    return null; // no etag
                }

                if (ifModifiedSince != null && ifUnmodifiedSince != null) // both exist
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest).ToTask();
                }
                bool ifModified = (ifUnmodifiedSince == null);
                DateTimeOffset modifiedInQuestion = ifModified ? ifModifiedSince.Value : ifUnmodifiedSince.Value;

                IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers =
                    request.Headers.Where(h => _varyByHeaders.Any(v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));
                string resource = UriTrimmer(request.RequestUri);
                EntityTagKey entityTagKey = EntityTagKeyGenerator(resource, headers);

                TimedEntityTagHeaderValue actualEtag;

                bool isModified = false;
                if (_entityTagStore.TryGetValue(entityTagKey, out actualEtag))
                {
                    isModified = actualEtag.LastModified > modifiedInQuestion;
                }

                return isModified ^ ifModified
                    ? new NotModifiedResponse(actualEtag.ToEntityTagHeaderValue()).ToTask()
                    : null;
            };
        }

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> PutIfUnmodifiedSince()
        {
            return request =>
            {
                if (request.Method != HttpMethod.Put)
                {
                    return null;
                }

                DateTimeOffset? ifUnmodifiedSince = request.Headers.IfUnmodifiedSince;
                if (ifUnmodifiedSince == null)
                {
                    return null;
                }

                DateTimeOffset modifiedInQuestion = ifUnmodifiedSince.Value;

                IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers =
                    request.Headers.Where(h => _varyByHeaders.Any(v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));
                string resource = UriTrimmer(request.RequestUri);
                EntityTagKey entityTagKey = EntityTagKeyGenerator(resource, headers);
                TimedEntityTagHeaderValue actualEtag;

                bool isModified = false;
                if (_entityTagStore.TryGetValue(entityTagKey, out actualEtag))
                {
                    isModified = actualEtag.LastModified > modifiedInQuestion;
                }

                return isModified
                    ? new HttpResponseMessage(HttpStatusCode.PreconditionFailed).ToTask()
                    : null;
            };
        }

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> PutIfMatch()
        {
            return request =>
            {
                if (request.Method != HttpMethod.Put)
                {
                    return null;
                }

                ICollection<EntityTagHeaderValue> matchTags = request.Headers.IfMatch;
                if (matchTags == null || matchTags.Count == 0)
                {
                    return null;
                }

                IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers =
                    request.Headers.Where(h => _varyByHeaders.Any(v => v.Equals(h.Key, StringComparison.CurrentCultureIgnoreCase)));
                string resource = UriTrimmer(request.RequestUri);
                EntityTagKey entityTagKey = EntityTagKeyGenerator(resource, headers);
                TimedEntityTagHeaderValue actualEtag;

                bool matchFound = false;
                if (_entityTagStore.TryGetValue(entityTagKey, out actualEtag))
                {
                    if (matchTags.Any(etag => etag.Tag == actualEtag.Tag))
                    {
                        matchFound = true;
                    }
                }

                return matchFound
                    ? null
                    : new HttpResponseMessage(HttpStatusCode.PreconditionFailed).ToTask();
            };
        }
    }
}