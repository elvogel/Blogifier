﻿using Blogifier.Core.Common;
using Blogifier.Core.Data.Domain;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Blogifier.Core.Data.Interfaces
{
    public interface IPostRepository : IRepository<Publication>
    {
        Task<List<PostListItem>> Find(Expression<Func<Publication, bool>> predicate, Pager pager);
        Task<List<PostListItem>> ByCategory(string slug, Pager pager, string blog = "");
        Task<Publication> SingleIncluded(Expression<Func<Publication, bool>> predicate);
        Task UpdatePostCategories(int postId, IEnumerable<string> catIds);
    }
}
