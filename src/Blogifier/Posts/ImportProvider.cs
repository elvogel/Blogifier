using Blogifier.Data;
using Blogifier.Extensions;
using Blogifier.Helper;
using Blogifier.Identity;
using Blogifier.Shared;
using Blogifier.Storages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Blogifier.Posts;

public class ImportProvider
{
  private readonly ILogger _logger;
  private readonly AppDbContext _dbContext;
  private readonly UserProvider _userProvider;
  private readonly MarkdigProvider _markdigProvider;
  private readonly PostProvider _postProvider;
  private readonly StorageProvider _storageProvider;

  public ImportProvider(
    ILogger<ImportProvider> logger,
    AppDbContext dbContext,
    UserProvider userProvider,
    MarkdigProvider markdigProvider,
    PostProvider postProvider,
    StorageProvider storageProvider)
  {
    _logger = logger;
    _dbContext = dbContext;
    _userProvider = userProvider;
    _markdigProvider = markdigProvider;
    _postProvider = postProvider;
    _storageProvider = storageProvider;
  }

  public async Task<IEnumerable<PostEditorDto>> WriteAsync(ImportDto request, string webRoot, string userId)
  {
    var user = await _userProvider.FindByIdAsync(userId);
    var titles = request.Posts.Select(m => m.Title);
    var matchPosts = await _postProvider.MatchTitleAsync(titles);

    var posts = new List<PostEditorDto>();

    foreach (var post in request.Posts)
    {
      var postDb = matchPosts.FirstOrDefault(m => m.Title.Equals(post.Title, StringComparison.Ordinal));
      if (postDb != null)
      {
        posts.Add(postDb);
        continue;
      }

      var publishedAt = post.PublishedAt!.Value;

      if (post.Cover != null && !post.Cover.Equals(BlogifierConstant.DefaultCover, StringComparison.Ordinal))
      {
        await _storageProvider.UploadFromWeb(webRoot, user.Id, post.Slug!, post.Cover, publishedAt);
      }
      var imgTagsMatches = StringHelper.MatchesImgTags(post.Content);
      if (imgTagsMatches.Any())
      {
        var contentBuilder = new StringBuilder(post.Content);
        foreach (Match match in imgTagsMatches.Cast<Match>())
        {
          var tag = match.Value;
          var matchUrl = StringHelper.MatchImgSrc(tag);
          var urlString = matchUrl.Groups[1].Value;
          var uploadTag = await _storageProvider.UploadFromWeb(webRoot, user.Id, post.Slug!, urlString, publishedAt);
          contentBuilder.Replace(tag, uploadTag);
        }
        post.Content = contentBuilder.ToString();
      }
      var markdownDescription = _markdigProvider.ToMarkdown(post.Description);
      _logger.LogDebug("ToMarkdown html: {} , markdown: {}", post.Description, markdownDescription);
      var markdownContent = _markdigProvider.ToMarkdown(post.Content);
      _logger.LogDebug("ToMarkdown html: {} , markdown: {}", post.Content, markdownContent);

      post.Description = markdownDescription;
      post.Content = markdownContent;
      post.State = PostState.Release;
      posts.Add(post);
    }

    return await _postProvider.AddAsync(posts, userId);
  }

  public async Task<bool> ImportPost(Post post)
  {
    try
    {
      await ImportImages(post);
      await ImportFiles(post);

      var converter = new ReverseMarkdown.Converter();

      post.Description = GetDescription(converter.Convert(post.Description));
      post.Content = converter.Convert(post.Content);

      await _dbContext.Posts.AddAsync(post);
      if (await _dbContext.SaveChangesAsync() == 0)
      {
        _logger.LogError("Error saving post {post.Title}", post.Title);
        return false;
      }

      var savedPost = await _dbContext.Posts.SingleAsync(p => p.Slug == post.Slug);
      if (savedPost == null)
      {
        _logger.LogError("Error finding saved post - {Title}", post.Title);
        return false;
      }
      return await _dbContext.SaveChangesAsync() > 0;
    }
    catch (Exception ex)
    {
      _logger.LogError("Error importing post {Title}: {Message}", post.Title, ex.Message);
      return false;
    }
  }

  #region Private members

  async Task ImportImages(Post post)
  {
    string rgx = @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>";

    if (string.IsNullOrEmpty(post.Content))
      return;

    if (post.Cover != BlogifierConstant.DefaultCover)
    {
      //var path = string.Format("{0}/{1}/{2}", post.AuthorId, post.PublishedAt.Year, post.PublishedAt.Month);

      //var mdTag = await _storageProvider.UploadFromWeb(new Uri(post.Cover), _webRoot, path);
      //if (mdTag.Length > 0 && mdTag.IndexOf("(") > 2)
      //  post.Cover = mdTag.Substring(mdTag.IndexOf("(") + 2).Replace(")", "");
    }

    var matches = Regex.Matches(post.Content, rgx, RegexOptions.IgnoreCase | RegexOptions.Singleline);

    if (matches == null)
      return;

    foreach (Match m in matches)
    {
      try
      {
        var tag = m.Groups[0].Value;
        //var path = string.Format("{0}/{1}/{2}", post.AuthorId, post.PublishedAt.Year, post.PublishedAt.Month);

        var uri = Regex.Match(tag, "<img.+?src=[\"'](.+?)[\"'].+?>", RegexOptions.IgnoreCase).Groups[1].Value;
        uri = ValidateUrl(uri);
        var mdTag = "";

        //if (uri.Contains("data:image"))
        //  mdTag = await _storageProvider.UploadBase64Image(uri, _webRoot, path);
        //else
        //{
        //  try
        //  {
        //    mdTag = await _storageProvider.UploadFromWeb(new Uri(uri), _webRoot, path);
        //  }
        //  catch
        //  {
        //    if (uri.StartsWith("https:"))
        //    {
        //      mdTag = await _storageProvider.UploadFromWeb(new Uri(uri.Replace("https:", "http:")), _webRoot, path);
        //    }
        //  }
        //}
        post.Content = post.Content.ReplaceIgnoreCase(tag, mdTag);
      }
      catch (Exception ex)
      {
        Serilog.Log.Error($"Error importing images: {ex.Message}");
      }
    }
  }

  async Task ImportFiles(Post post)
  {
    var rgx = @"(?i)<a\b[^>]*?>(?<text>.*?)</a>";
    var exts = new string[] { "zip", "7z", "xml", "pdf", "doc", "docx", "xls", "xlsx", "mp3", "mp4", "avi" };
    if (string.IsNullOrEmpty(post.Content)) return;
    var matches = Regex.Matches(post.Content, rgx, RegexOptions.IgnoreCase | RegexOptions.Singleline);

    if (matches != null)
    {
      foreach (Match m in matches)
      {
        try
        {
          var tag = m.Value;
          var src = XElement.Parse(tag).Attribute("href")!.Value;
          var mdTag = "";

          foreach (var ext in exts)
          {
            if (src.ToLower().EndsWith($".{ext}"))
            {
              var uri = ValidateUrl(src);
              //var path = string.Format("{0}/{1}/{2}", post.AuthorId, post.PublishedAt.Year, post.PublishedAt.Month);

              //mdTag = await _storageProvider.UploadFromWeb(new Uri(uri), _webRoot, path);

              if (mdTag.StartsWith("!"))
                mdTag = mdTag.Substring(1);

              post.Content = post.Content.ReplaceIgnoreCase(m.Value, mdTag);
            }
          }
        }
        catch (Exception ex)
        {
          Serilog.Log.Error($"Error importing files: {ex.Message}");
        }
      }
    }
  }

  async Task<string> GetSlug(string title)
  {
    string slug = title.ToSlug();
    Post post = await _dbContext.Posts.SingleOrDefaultAsync(p => p.Slug == slug);

    if (post == null)
      return slug;

    for (int i = 2; i < 100; i++)
    {
      post = await _dbContext.Posts.AsNoTracking()
          .SingleAsync(p => p.Slug == $"{slug}{i}");

      if (post == null)
        return await Task.FromResult(slug + i.ToString());
    }
    return slug;
  }

  string ValidateUrl(string link)
  {
    var url = link;

    //var baseUrl = _baseUrl.ToString();
    //if (baseUrl.EndsWith("/"))
    //  baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);

    //if (url.StartsWith("~"))
    //  url = url.Replace("~", baseUrl);

    //if (url.StartsWith("/"))
    //  url = $"{baseUrl}{url}";

    //if (!(url.StartsWith("http:") || url.StartsWith("https:")))
    //  url = $"{baseUrl}/{url}";

    return url;
  }
  static string GetDescription(string description)
  {
    description = description.StripHtml();
    if (description.Length > 450) description = description.Substring(0, 446) + "...";
    return description;
  }


  #endregion
}
