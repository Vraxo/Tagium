using Microsoft.Extensions.Logging;
using System.Text;
using Tagium.Core.Settings;

namespace Tagium.Core.Search;

public record CompiledQuery(string Sql, List<(string Name, object Value)> Parameters);

public class SearchQueryCompiler
{
    private readonly ILogger<SearchQueryCompiler> _logger;

    /// <summary>The category keys accepted by the search language.</summary>
    public static IReadOnlySet<string> CategoryKeys => SearchQueryParser.CategoryKeys;

    public SearchQueryCompiler(ILogger<SearchQueryCompiler> logger)
    {
        _logger = logger;
    }

    public CompiledQuery Compile(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        IReadOnlyList<SearchFilter> filters = SearchQueryParser.Parse(searchText, _logger);

        StringBuilder query = new();
        query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime, F.Rating, F.Title, F.Description");
        query.AppendLine("FROM cache.Files F");
        query.AppendLine("WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '" + Path.DirectorySeparatorChar + "'))");

        List<(string Name, object Value)> parameters = [];
        int paramCounter = 0;

        foreach (SearchFilter filter in filters)
        {
            switch (filter)
            {
                case RatingFilter rating:
                    BuildRatingClause(rating, query, parameters, ref paramCounter);
                    break;
                case SizeFilter size:
                    BuildSizeClause(size, query, parameters, ref paramCounter);
                    break;
                case ExtensionFilter extension:
                    BuildExtensionClause(extension, query, parameters, ref paramCounter);
                    break;
                case CategoryFilter category:
                    BuildCategoryClause(category.Category, category.Text, category.IsNegated, query, parameters, ref paramCounter);
                    break;
                case TagFilter tag:
                    BuildTagClause(tag.Text, tag.IsNegated, query, parameters, ref paramCounter);
                    break;
                case MissingFilter missing:
                    BuildMissingClause(missing, query);
                    break;
                case HasFilter has:
                    BuildHasClause(has, query);
                    break;
                case DateFilter date:
                    BuildDateClause(date, query, parameters, ref paramCounter);
                    break;
                case UntaggedFilter untagged:
                    BuildUntaggedClause(untagged.IsNegated, query);
                    break;
                case TextFilter text:
                    BuildTagSearchClause(text.Text, text.IsNegated, query, parameters, ref paramCounter);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported search filter: {filter.GetType().Name}");
            }
        }

        query.AppendLine(GetOrderByClause(sortBy, sortDirection));

        return new CompiledQuery(query.ToString(), parameters);
    }

    private static void BuildRatingClause(RatingFilter filter, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        parameters.Add((paramName, filter.Rating));
        string op = GetOperator(filter.Operator);
        if (filter.IsNegated)
        {
            query.AppendLine($"AND (F.Rating IS NULL OR NOT (F.Rating {op} {paramName}))");
        }
        else
        {
            query.AppendLine($"AND (F.Rating IS NOT NULL AND F.Rating {op} {paramName})");
        }
    }

    private static void BuildSizeClause(SizeFilter filter, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        parameters.Add((paramName, filter.Bytes));
        string op = GetOperator(filter.Operator);
        if (filter.IsNegated)
        {
            query.AppendLine($"AND NOT (F.Size {op} {paramName})");
        }
        else
        {
            query.AppendLine($"AND F.Size {op} {paramName}");
        }
    }

    private static void BuildExtensionClause(ExtensionFilter filter, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        parameters.Add((paramName, "%." + filter.Extension));
        if (filter.IsNegated)
        {
            query.AppendLine($"AND F.Path NOT LIKE {paramName}");
        }
        else
        {
            query.AppendLine($"AND F.Path LIKE {paramName}");
        }
    }

    private static void BuildCategoryClause(string key, string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string searchPattern = "%" + val + "%";
        string catParam = $"@p{paramCounter++}";
        string valParam = $"@p{paramCounter++}";

        parameters.Add((catParam, key));
        parameters.Add((valParam, searchPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND T.Category = {catParam} AND (T.Name LIKE {valParam} OR TA.Alias LIKE {valParam})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT ({subQuery})");
        }
        else
        {
            query.AppendLine($"AND ({subQuery})");
        }
    }

    private static void BuildTagClause(string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        parameters.Add((paramName, $"%{val}%"));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND (T.Name LIKE {paramName} OR TA.Alias LIKE {paramName})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT ({subQuery})");
        }
        else
        {
            query.AppendLine($"AND ({subQuery})");
        }
    }

    private static void BuildUntaggedClause(bool isNegated, StringBuilder query)
    {
        const string hasTags = "EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id)";

        if (isNegated)
        {
            query.AppendLine($"AND {hasTags}");
        }
        else
        {
            query.AppendLine($"AND NOT {hasTags}");
        }
    }

    private static void BuildMissingClause(MissingFilter filter, StringBuilder query)
    {
        bool missing = filter.IsNegated ? !filter.Missing : filter.Missing;
        query.AppendLine(missing ? "AND F.IsMissing = 1" : "AND F.IsMissing = 0");
    }

    private static void BuildHasClause(HasFilter filter, StringBuilder query)
    {
        string positive = filter.Field switch
        {
            SearchPresenceField.Rating => "F.Rating IS NOT NULL",
            SearchPresenceField.Title => "F.Title IS NOT NULL AND F.Title <> ''",
            SearchPresenceField.Description => "F.Description IS NOT NULL AND F.Description <> ''",
            SearchPresenceField.Tags => "EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id)",
            _ => throw new InvalidOperationException("Unsupported presence field")
        };

        string negative = filter.Field switch
        {
            SearchPresenceField.Rating => "F.Rating IS NULL",
            SearchPresenceField.Title => "F.Title IS NULL OR F.Title = ''",
            SearchPresenceField.Description => "F.Description IS NULL OR F.Description = ''",
            SearchPresenceField.Tags => "NOT EXISTS (SELECT 1 FROM cache.FileTags FT WHERE FT.FileId = F.Id)",
            _ => throw new InvalidOperationException("Unsupported presence field")
        };

        query.AppendLine(filter.IsNegated ? $"AND ({negative})" : $"AND ({positive})");
    }

    private static void BuildDateClause(DateFilter filter, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string column = filter.Field switch
        {
            SearchDateField.Added => "F.DateAdded",
            SearchDateField.Modified => "F.ModifiedTime",
            _ => throw new InvalidOperationException("Unsupported date field")
        };
        string op = GetOperator(filter.Operator);
        long timestamp = new DateTimeOffset(filter.Date).ToUnixTimeSeconds();
        string paramName = $"@p{paramCounter++}";
        parameters.Add((paramName, timestamp));

        if (filter.IsNegated)
        {
            query.AppendLine($"AND NOT ({column} {op} {paramName})");
        }
        else
        {
            query.AppendLine($"AND {column} {op} {paramName}");
        }
    }

    private static void BuildTagSearchClause(string token, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        string termPattern = $"%{token}%";
        parameters.Add((paramName, termPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND (T.Name LIKE {paramName} OR TA.Alias LIKE {paramName})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
        else
        {
            query.AppendLine($"AND (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
    }

    private static string GetOperator(SearchComparison comparison) => comparison switch
    {
        SearchComparison.Equal => "=",
        SearchComparison.GreaterThan => ">",
        SearchComparison.GreaterThanOrEqual => ">=",
        SearchComparison.LessThan => "<",
        SearchComparison.LessThanOrEqual => "<=",
        _ => throw new InvalidOperationException("Unsupported comparison operator")
    };

    private static string GetOrderByClause(SortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            SortBy.Name => "F.Name",
            SortBy.DateAdded => "F.DateAdded",
            SortBy.Size => "F.Size",
            SortBy.ModifiedTime => "F.ModifiedTime",
            SortBy.Rating => "F.Rating",
            _ => "F.Path"
        };

        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}, F.Path ASC";
    }
}