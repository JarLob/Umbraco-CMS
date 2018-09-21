﻿using System;
using System.Collections.Generic;
using System.Linq;
using NPoco;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Entities;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Dtos;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Scoping;
using static Umbraco.Core.Persistence.NPocoSqlExtensions.Statics;
using Umbraco.Core.Persistence.SqlSyntax;

namespace Umbraco.Core.Persistence.Repositories.Implement
{
    // fixme - use sql templates everywhere!

    /// <summary>
    /// Represents the EntityRepository used to query entity objects.
    /// </summary>
    /// <remarks>
    /// <para>Limited to objects that have a corresponding node (in umbracoNode table).</para>
    /// <para>Returns <see cref="IEntitySlim"/> objects, i.e. lightweight representation of entities.</para>
    /// </remarks>
    internal class EntityRepository : IEntityRepository
    {
        private readonly IScopeAccessor _scopeAccessor;
        private readonly ILanguageRepository _langRepository;

        public EntityRepository(IScopeAccessor scopeAccessor, ILanguageRepository langRepository)
        {
            _scopeAccessor = scopeAccessor;
            _langRepository = langRepository;
        }

        protected IUmbracoDatabase Database => _scopeAccessor.AmbientScope.Database;
        protected Sql<ISqlContext> Sql() => _scopeAccessor.AmbientScope.SqlContext.Sql();
        protected ISqlSyntaxProvider SqlSyntax => _scopeAccessor.AmbientScope.SqlContext.SqlSyntax;

        #region Repository

        // get a page of entities
        public IEnumerable<IEntitySlim> GetPagedResultsByQuery(IQuery<IUmbracoEntity> query, Guid objectType, long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, IQuery<IUmbracoEntity> filter = null)
        {
            var isContent = objectType == Constants.ObjectTypes.Document || objectType == Constants.ObjectTypes.DocumentBlueprint;
            var isMedia = objectType == Constants.ObjectTypes.Media;

            var sql = GetBaseWhere(isContent, isMedia, false, x =>
            {
                if (filter == null) return;
                foreach (var filterClause in filter.GetWhereClauses())
                    x.Where(filterClause.Item1, filterClause.Item2);
            }, objectType);

            var translator = new SqlTranslator<IUmbracoEntity>(sql, query);
            sql = translator.Translate();
            sql = AddGroupBy(isContent, isMedia, sql);
            //fixme - we should be able to do sql = sql.OrderBy(x => Alias(x.NodeId, "NodeId")); but we can't because the OrderBy extension don't support Alias currently
            sql = sql.OrderBy("NodeId");


            var page = Database.Page<BaseDto>(pageIndex + 1, pageSize, sql);
            var dtos = page.Items;
            var entities = dtos.Select(x => BuildEntity(isContent, isMedia, x)).ToArray();
            
            if (isContent)
                BuildVariantInfo(entities);

            if (isMedia)
                BuildProperties(entities, dtos);

            totalRecords = page.TotalItems;
            return entities;
        }

        public IEntitySlim Get(Guid key)
        {
            var sql = GetBaseWhere(false, false, false, key);
            var dto = Database.FirstOrDefault<BaseDto>(sql);
            return dto == null ? null : BuildEntity(false, false, dto);
        }

        private IEntitySlim GetEntity(Sql<ISqlContext> sql, bool isContent, bool isMedia)
        {
            //isContent is going to return a 1:M result now with the variants so we need to do different things
            if (isContent)
            {
                var dtos = Database.FetchOneToMany<ContentEntityDto>(
                    ddto => ddto.VariationInfo,
                    ddto => ddto.VersionId,
                    sql);
                
                return dtos.Count == 0 ? null : BuildVariantInfo(BuildDocumentEntity(dtos[0]))[0];
            }

            var dto = Database.FirstOrDefault<BaseDto>(sql);
            if (dto == null) return null;

            var entity = BuildEntity(false, isMedia, dto);

            if (isMedia)
                BuildProperties(entity, dto);

            return entity;
        }

        public IEntitySlim Get(Guid key, Guid objectTypeId)
        {
            var isContent = objectTypeId == Constants.ObjectTypes.Document || objectTypeId == Constants.ObjectTypes.DocumentBlueprint;
            var isMedia = objectTypeId == Constants.ObjectTypes.Media;

            var sql = GetFullSqlForEntityType(isContent, isMedia, objectTypeId, key);
            return GetEntity(sql, isContent, isMedia);
        }

        public virtual IEntitySlim Get(int id)
        {
            var sql = GetBaseWhere(false, false, false, id);
            var dto = Database.FirstOrDefault<BaseDto>(sql);
            return dto == null ? null : BuildEntity(false, false, dto);
        }

        public virtual IEntitySlim Get(int id, Guid objectTypeId)
        {
            var isContent = objectTypeId == Constants.ObjectTypes.Document || objectTypeId == Constants.ObjectTypes.DocumentBlueprint;
            var isMedia = objectTypeId == Constants.ObjectTypes.Media;

            var sql = GetFullSqlForEntityType(isContent, isMedia, objectTypeId, id);
            return GetEntity(sql, isContent, isMedia);
        }

        public virtual IEnumerable<IEntitySlim> GetAll(Guid objectType, params int[] ids)
        {
            return ids.Length > 0
                ? PerformGetAll(objectType, sql => sql.WhereIn<NodeDto>(x => x.NodeId, ids.Distinct()))
                : PerformGetAll(objectType);
        }

        public virtual IEnumerable<IEntitySlim> GetAll(Guid objectType, params Guid[] keys)
        {
            return keys.Length > 0
                ? PerformGetAll(objectType, sql => sql.WhereIn<NodeDto>(x => x.UniqueId, keys.Distinct()))
                : PerformGetAll(objectType);
        }

        private IEnumerable<IEntitySlim> GetEntities(Sql<ISqlContext> sql, bool isContent, bool isMedia)
        {
            //isContent is going to return a 1:M result now with the variants so we need to do different things
            if (isContent)
            {
                var cdtos = Database.FetchOneToMany<ContentEntityDto>(
                    dto => dto.VariationInfo,
                    dto => dto.VersionId,
                    sql);

                return cdtos.Count == 0
                    ? Enumerable.Empty<IEntitySlim>()
                    : BuildVariantInfo(cdtos.Select(BuildDocumentEntity).ToArray()).ToList();
            }

            var dtos = Database.Fetch<BaseDto>(sql);
            if (dtos.Count == 0) return Enumerable.Empty<IEntitySlim>();

            var entities = dtos.Select(x => BuildEntity(false, isMedia, x)).ToArray();

            if (isMedia)
                BuildProperties(entities, dtos);

            return entities;
        }

        private IEnumerable<IEntitySlim> PerformGetAll(Guid objectType, Action<Sql<ISqlContext>> filter = null)
        {
            var isContent = objectType == Constants.ObjectTypes.Document || objectType == Constants.ObjectTypes.DocumentBlueprint;
            var isMedia = objectType == Constants.ObjectTypes.Media;

            var sql = GetFullSqlForEntityType(isContent, isMedia, objectType, filter);
            return GetEntities(sql, isContent, isMedia);
        }

        public virtual IEnumerable<TreeEntityPath> GetAllPaths(Guid objectType, params int[] ids)
        {
            return ids.Any()
                ? PerformGetAllPaths(objectType, sql => sql.WhereIn<NodeDto>(x => x.NodeId, ids.Distinct()))
                : PerformGetAllPaths(objectType);
        }

        public virtual IEnumerable<TreeEntityPath> GetAllPaths(Guid objectType, params Guid[] keys)
        {
            return keys.Any()
                ? PerformGetAllPaths(objectType, sql => sql.WhereIn<NodeDto>(x => x.UniqueId, keys.Distinct()))
                : PerformGetAllPaths(objectType);
        }

        private IEnumerable<TreeEntityPath> PerformGetAllPaths(Guid objectType, Action<Sql<ISqlContext>> filter = null)
        {
            var sql = Sql().Select<NodeDto>(x => x.NodeId, x => x.Path).From<NodeDto>().Where<NodeDto>(x => x.NodeObjectType == objectType);
            filter?.Invoke(sql);
            return Database.Fetch<TreeEntityPath>(sql);
        }

        public virtual IEnumerable<IEntitySlim> GetByQuery(IQuery<IUmbracoEntity> query)
        {
            var sqlClause = GetBase(false, false, null);
            var translator = new SqlTranslator<IUmbracoEntity>(sqlClause, query);
            var sql = translator.Translate();
            sql = AddGroupBy(false, false, sql);
            var dtos = Database.Fetch<BaseDto>(sql);
            return dtos.Select(x => BuildEntity(false, false, x)).ToList();
        }

        public virtual IEnumerable<IEntitySlim> GetByQuery(IQuery<IUmbracoEntity> query, Guid objectType)
        {
            var isContent = objectType == Constants.ObjectTypes.Document || objectType == Constants.ObjectTypes.DocumentBlueprint;
            var isMedia = objectType == Constants.ObjectTypes.Media;

            var sql = GetBaseWhere(isContent, isMedia, false, null, objectType);

            var translator = new SqlTranslator<IUmbracoEntity>(sql, query);
            sql = translator.Translate();
            sql = AddGroupBy(isContent, isMedia, sql);

            return GetEntities(sql, isContent, isMedia);
        }

        public UmbracoObjectTypes GetObjectType(int id)
        {
            var sql = Sql().Select<NodeDto>(x => x.NodeObjectType).From<NodeDto>().Where<NodeDto>(x => x.NodeId == id);
            return ObjectTypes.GetUmbracoObjectType(Database.ExecuteScalar<Guid>(sql));
        }

        public UmbracoObjectTypes GetObjectType(Guid key)
        {
            var sql = Sql().Select<NodeDto>(x => x.NodeObjectType).From<NodeDto>().Where<NodeDto>(x => x.UniqueId == key);
            return ObjectTypes.GetUmbracoObjectType(Database.ExecuteScalar<Guid>(sql));
        }

        public bool Exists(Guid key)
        {
            var sql = Sql().SelectCount().From<NodeDto>().Where<NodeDto>(x => x.UniqueId == key);
            return Database.ExecuteScalar<int>(sql) > 0;
        }

        public bool Exists(int id)
        {
            var sql = Sql().SelectCount().From<NodeDto>().Where<NodeDto>(x => x.NodeId == id);
            return Database.ExecuteScalar<int>(sql) > 0;
        }

        private void BuildProperties(EntitySlim entity, BaseDto dto)
        {
            var pdtos = Database.Fetch<PropertyDataDto>(GetPropertyData(dto.VersionId));
            foreach (var pdto in pdtos)
                BuildProperty(entity, pdto);
        }

        private void BuildProperties(EntitySlim[] entities, List<BaseDto> dtos)
        {
            var versionIds = dtos.Select(x => x.VersionId).Distinct().ToList();
            var pdtos = Database.FetchByGroups<PropertyDataDto, int>(versionIds, 2000, GetPropertyData);

            var xentity = entities.ToDictionary(x => x.Id, x => x); // nodeId -> entity
            var xdto = dtos.ToDictionary(x => x.VersionId, x => x.NodeId); // versionId -> nodeId
            foreach (var pdto in pdtos)
            {
                var nodeId = xdto[pdto.VersionId];
                var entity = xentity[nodeId];
                BuildProperty(entity, pdto);
            }
        }

        private void BuildProperty(EntitySlim entity, PropertyDataDto pdto)
        {
            // explain ?!
            var value = string.IsNullOrWhiteSpace(pdto.TextValue)
                ? pdto.VarcharValue
                : pdto.TextValue.ConvertToJsonIfPossible();

            entity.AdditionalData[pdto.PropertyTypeDto.Alias] = new EntitySlim.PropertySlim(pdto.PropertyTypeDto.DataTypeDto.EditorAlias, value);
        }

        private void BuildVariantInfo(EntitySlim[] entities)
        {
            BuildVariantInfo((DocumentEntitySlim[])entities);
        }

        private DocumentEntitySlim[] BuildVariantInfo(params DocumentEntitySlim[] entities)
        {
            if (entities.Any(x => x.Variations.VariesByCulture()))
            {
                //each EntitySlim at this stage is an DocumentEntitySlim

                var dtos = Database.FetchByGroups<VariationPublishInfoDto, int>(entities.Select(x => x.Id), 2000, GetVariantPublishedInfo)
                    .GroupBy(x => x.NodeId)
                    .ToDictionary(x => x.Key, x => (IEnumerable<VariationPublishInfoDto>)x);

                foreach (var e in entities.OfType<DocumentEntitySlim>().Where(x => x.Variations.VariesByCulture()).OrderBy(x => x.Id))
                {
                    //fixme: how do i get this info? Seems that requires another query since that is how I think it's done in the DocumentRepository
                    //e.EditedCultures =
                    e.PublishedCultures = dtos[e.Id].Where(x => x.VersionPublished).Select(x => x.IsoCode).Distinct().ToList();
                }
            }

            return entities;
        }

        #endregion

        #region Sql

        private Sql<ISqlContext> GetVariantPublishedInfo(IEnumerable<int> ids)
        {
            var sql = Sql();
            sql
                .Select<ContentVersionDto>(x => x.NodeId, x => Alias(x.Id, "versionId"), x => Alias(x.Current, "versionCurrent"))
                .AndSelect<ContentVersionCultureVariationDto>(x => Alias(x.Id, "versionCultureId"))
                .AndSelect<LanguageDto>(x => x.IsoCode)
                .AndSelect<DocumentVersionDto>(x => Alias(x.Published, "versionPublished"))
                .From<NodeDto>()
                .InnerJoin<ContentVersionDto>().On<NodeDto, ContentVersionDto>(x => x.NodeId, x => x.NodeId)
                .InnerJoin<ContentVersionCultureVariationDto>().On<ContentVersionDto, ContentVersionCultureVariationDto>(x => x.Id, x => x.VersionId)
                .InnerJoin<DocumentVersionDto>().On<ContentVersionDto, DocumentVersionDto>(x => x.Id, x => x.Id)
                .InnerJoin<LanguageDto>().On<ContentVersionCultureVariationDto, LanguageDto>(x => x.LanguageId, x => x.Id)
                .Where<NodeDto>(x => x.NodeObjectType == Constants.ObjectTypes.Document)
                .WhereIn<NodeDto>(x => x.NodeId, ids)
                .Where($"{SqlSyntax.GetFieldName<ContentVersionDto>(x => x.Current)} = 1 OR {SqlSyntax.GetFieldName<DocumentVersionDto>(x => x.Published)} = 1");
            return sql;
        }
        // gets the full sql for a given object type and a given unique id
        protected Sql<ISqlContext> GetFullSqlForEntityType(bool isContent, bool isMedia, Guid objectType, Guid uniqueId)
        {
            var sql = GetBaseWhere(isContent, isMedia, false, objectType, uniqueId);
            return AddGroupBy(isContent, isMedia, sql);
        }

        // gets the full sql for a given object type and a given node id
        protected Sql<ISqlContext> GetFullSqlForEntityType(bool isContent, bool isMedia, Guid objectType, int nodeId)
        {
            var sql = GetBaseWhere(isContent, isMedia, false, objectType, nodeId);
            return AddGroupBy(isContent, isMedia, sql);
        }

        // gets the full sql for a given object type, with a given filter
        protected Sql<ISqlContext> GetFullSqlForEntityType(bool isContent, bool isMedia, Guid objectType, Action<Sql<ISqlContext>> filter)
        {
            var sql = GetBaseWhere(isContent, isMedia, false, filter, objectType);
            return AddGroupBy(isContent, isMedia, sql);
        }

        private Sql<ISqlContext> GetPropertyData(int versionId)
        {
            return Sql()
                .Select<PropertyDataDto>(r => r.Select(x => x.PropertyTypeDto, r1 => r1.Select(x => x.DataTypeDto)))
                .From<PropertyDataDto>()
                .InnerJoin<PropertyTypeDto>().On<PropertyDataDto, PropertyTypeDto>((left, right) => left.PropertyTypeId == right.Id)
                .InnerJoin<DataTypeDto>().On<PropertyTypeDto, DataTypeDto>((left, right) => left.DataTypeId == right.NodeId)
                .Where<PropertyDataDto>(x => x.VersionId == versionId);
        }

        private Sql<ISqlContext> GetPropertyData(IEnumerable<int> versionIds)
        {
            return Sql()
                .Select<PropertyDataDto>(r => r.Select(x => x.PropertyTypeDto, r1 => r1.Select(x => x.DataTypeDto)))
                .From<PropertyDataDto>()
                .InnerJoin<PropertyTypeDto>().On<PropertyDataDto, PropertyTypeDto>((left, right) => left.PropertyTypeId == right.Id)
                .InnerJoin<DataTypeDto>().On<PropertyTypeDto, DataTypeDto>((left, right) => left.DataTypeId == right.NodeId)
                .WhereIn<PropertyDataDto>(x => x.VersionId, versionIds)
                .OrderBy<PropertyDataDto>(x => x.VersionId);
        }

        

        // gets the base SELECT + FROM [+ filter] sql
        // always from the 'current' content version
        protected virtual Sql<ISqlContext> GetBase(bool isContent, bool isMedia, Action<Sql<ISqlContext>> filter, bool isCount = false)
        {
            var sql = Sql();

            if (isCount)
            {
                sql.SelectCount();
            }
            else
            {
                sql
                    .Select<NodeDto>(x => x.NodeId, x => x.Trashed, x => x.ParentId, x => x.UserId, x => x.Level, x => x.Path)
                    .AndSelect<NodeDto>(x => x.SortOrder, x => x.UniqueId, x => x.Text, x => x.NodeObjectType, x => x.CreateDate)
                    .Append(", COUNT(child.id) AS children");

                if (isContent || isMedia)
                    sql
                        .AndSelect<ContentVersionDto>(x => Alias(x.Id, "versionId"))
                        .AndSelect<ContentTypeDto>(x => x.Alias, x => x.Icon, x => x.Thumbnail, x => x.IsContainer, x => x.Variations);

                if (isContent)
                {
                    sql
                        .AndSelect<DocumentDto>(x => x.Published, x => x.Edited)
                        //This MUST come last in the select statements since we will end up with a 1:M query
                        .AndSelect<ContentVersionCultureVariationDto>(
                            x => Alias(x.Id, "versionCultureId"),
                            x => Alias(x.LanguageId, "versionCultureLangId"),
                            x => Alias(x.Name, "versionCultureName"));
                }
            }

            sql
                .From<NodeDto>();

            if (isContent || isMedia)
            {
                sql
                    .InnerJoin<ContentVersionDto>().On<NodeDto, ContentVersionDto>((left, right) => left.NodeId == right.NodeId && right.Current)
                    .InnerJoin<ContentDto>().On<NodeDto, ContentDto>((left, right) => left.NodeId == right.NodeId)
                    .InnerJoin<ContentTypeDto>().On<ContentDto, ContentTypeDto>((left, right) => left.ContentTypeId == right.NodeId);
            }

            if (isContent)
            {
                sql
                    .InnerJoin<DocumentDto>().On<NodeDto, DocumentDto>((left, right) => left.NodeId == right.NodeId);
            }

            //Any LeftJoin statements need to come last
            if (isCount == false)
            {
                sql
                    .LeftJoin<NodeDto>("child").On<NodeDto, NodeDto>((left, right) => left.NodeId == right.ParentId, aliasRight: "child");

                if (isContent)
                    sql
                        .LeftJoin<ContentVersionCultureVariationDto>().On<ContentVersionDto, ContentVersionCultureVariationDto>((left, right) => left.Id == right.VersionId);
            }


            filter?.Invoke(sql);

            return sql;
        }

        // gets the base SELECT + FROM [+ filter] + WHERE sql
        // for a given object type, with a given filter
        protected virtual Sql<ISqlContext> GetBaseWhere(bool isContent, bool isMedia, bool isCount, Action<Sql<ISqlContext>> filter, Guid objectType)
        {
            return GetBase(isContent, isMedia, filter, isCount)
                .Where<NodeDto>(x => x.NodeObjectType == objectType);
        }

        // gets the base SELECT + FROM + WHERE sql
        // for a given node id
        protected virtual Sql<ISqlContext> GetBaseWhere(bool isContent, bool isMedia, bool isCount, int id)
        {
            var sql = GetBase(isContent, isMedia, null, isCount)
                .Where<NodeDto>(x => x.NodeId == id);
            return AddGroupBy(isContent, isMedia, sql);
        }

        // gets the base SELECT + FROM + WHERE sql
        // for a given unique id
        protected virtual Sql<ISqlContext> GetBaseWhere(bool isContent, bool isMedia, bool isCount, Guid uniqueId)
        {
            var sql = GetBase(isContent, isMedia, null, isCount)
                .Where<NodeDto>(x => x.UniqueId == uniqueId);
            return AddGroupBy(isContent, isMedia, sql);
        }

        // gets the base SELECT + FROM + WHERE sql
        // for a given object type and node id
        protected virtual Sql<ISqlContext> GetBaseWhere(bool isContent, bool isMedia, bool isCount, Guid objectType, int nodeId)
        {
            return GetBase(isContent, isMedia, null, isCount)
                .Where<NodeDto>(x => x.NodeId == nodeId && x.NodeObjectType == objectType);
        }

        // gets the base SELECT + FROM + WHERE sql
        // for a given object type and unique id
        protected virtual Sql<ISqlContext> GetBaseWhere(bool isContent, bool isMedia, bool isCount, Guid objectType, Guid uniqueId)
        {
            return GetBase(isContent, isMedia, null, isCount)
                .Where<NodeDto>(x => x.UniqueId == uniqueId && x.NodeObjectType == objectType);
        }

        // gets the GROUP BY / ORDER BY sql
        // required in order to count children
        protected virtual Sql<ISqlContext> AddGroupBy(bool isContent, bool isMedia, Sql<ISqlContext> sql, bool sort = true)
        {
            sql
                .GroupBy<NodeDto>(x => x.NodeId, x => x.Trashed, x => x.ParentId, x => x.UserId, x => x.Level, x => x.Path)
                .AndBy<NodeDto>(x => x.SortOrder, x => x.UniqueId, x => x.Text, x => x.NodeObjectType, x => x.CreateDate);

            if (isContent)
            {
                sql
                    .AndBy<DocumentDto>(x => x.Published, x => x.Edited)
                    .AndBy<ContentVersionCultureVariationDto>(x => x.Id, x => x.LanguageId, x => x.Name);
            }


            if (isContent || isMedia)
                sql
                    .AndBy<ContentVersionDto>(x => x.Id)
                    .AndBy<ContentTypeDto>(x => x.Alias, x => x.Icon, x => x.Thumbnail, x => x.IsContainer, x => x.Variations);

            if (sort)
                sql.OrderBy<NodeDto>(x => x.SortOrder);

            return sql;
        }

        #endregion

        #region Classes

        [ExplicitColumns]
        internal class UmbracoPropertyDto
        {
            [Column("propertyEditorAlias")]
            public string PropertyEditorAlias { get; set; }

            [Column("propertyTypeAlias")]
            public string PropertyAlias { get; set; }

            [Column("varcharValue")]
            public string VarcharValue { get; set; }

            [Column("textValue")]
            public string TextValue { get; set; }
        }


        /// <summary>
        /// The DTO used to fetch results for a content item with its variation info
        /// </summary>
        private class ContentEntityDto : BaseDto
        {
            public ContentVariation Variations { get; set; }

            [ResultColumn, Reference(ReferenceType.Many)]
            public List<ContentEntityVariationInfoDto> VariationInfo { get; set; }

            public bool Published { get; set; }
            public bool Edited { get; set; }
        }

        private class VariationPublishInfoDto
        {
            public int NodeId { get; set; }
            public int VersionId { get; set; }
            public bool VersionCurrent { get; set; }
            public string IsoCode { get; set; }
            public int VersionCultureId { get; set; }
            public bool VersionPublished { get; set; }
        }

        /// <summary>
        /// The DTO used in the 1:M result for content variation info
        /// </summary>
        private class ContentEntityVariationInfoDto
        {
            [Column("versionCultureId")]
            public int VersionCultureId { get; set; }
            [Column("versionCultureLangId")]
            public int LanguageId { get; set; }
            [Column("versionCultureName")]
            public string Name { get; set; }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        /// <summary>
        /// the DTO corresponding to fields selected by GetBase
        /// </summary>
        private class BaseDto
        {
            // ReSharper disable UnusedAutoPropertyAccessor.Local
            // ReSharper disable UnusedMember.Local
            public int NodeId { get; set; }
            public bool Trashed { get; set; }
            public int ParentId { get; set; }
            public int? UserId { get; set; }
            public int Level { get; set; }
            public string Path { get; set; }
            public int SortOrder { get; set; }
            public Guid UniqueId { get; set; }
            public string Text { get; set; }
            public Guid NodeObjectType { get; set; }
            public DateTime CreateDate { get; set; }
            public int Children { get; set; }
            public int VersionId { get; set; }
            public string Alias { get; set; }
            public string Icon { get; set; }
            public string Thumbnail { get; set; }
            public bool IsContainer { get; set; }

            // ReSharper restore UnusedAutoPropertyAccessor.Local
            // ReSharper restore UnusedMember.Local
        }
        #endregion

        #region Factory

        private EntitySlim BuildEntity(bool isContent, bool isMedia, BaseDto dto)
        {
            if (isContent)
                return BuildDocumentEntity(dto);
            if (isMedia)
                return BuildContentEntity(dto);

            // EntitySlim does not track changes
            var entity = new EntitySlim();
            BuildEntity(entity, dto);
            return entity;
        }

        private static void BuildEntity(EntitySlim entity, BaseDto dto)
        {
            entity.Trashed = dto.Trashed;
            entity.CreateDate = dto.CreateDate;
            entity.CreatorId = dto.UserId ?? Constants.Security.UnknownUserId;
            entity.Id = dto.NodeId;
            entity.Key = dto.UniqueId;
            entity.Level = dto.Level;
            entity.Name = dto.Text;
            entity.NodeObjectType = dto.NodeObjectType;
            entity.ParentId = dto.ParentId;
            entity.Path = dto.Path;
            entity.SortOrder = dto.SortOrder;
            entity.HasChildren = dto.Children > 0;
            entity.IsContainer = dto.IsContainer;
        }

        private static void BuildContentEntity(ContentEntitySlim entity, BaseDto dto)
        {
            BuildEntity(entity, dto);
            entity.ContentTypeAlias = dto.Alias;
            entity.ContentTypeIcon = dto.Icon;
            entity.ContentTypeThumbnail = dto.Thumbnail;
        }

        private static EntitySlim BuildContentEntity(BaseDto dto)
        {
            // EntitySlim does not track changes
            var entity = new ContentEntitySlim();
            BuildContentEntity(entity, dto);
            return entity;
        }

        private DocumentEntitySlim BuildDocumentEntity(BaseDto dto)
        {
            if (dto is ContentEntityDto contentDto)
            {
                return BuildDocumentEntity(contentDto);
            }

            // EntitySlim does not track changes
            var entity = new DocumentEntitySlim();
            BuildContentEntity(entity, dto);
            return entity;
        }

        /// <summary>
        /// Builds the <see cref="EntitySlim"/> from a <see cref="ContentEntityDto"/> and ensures the AdditionalData is populated with variant info
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        private DocumentEntitySlim BuildDocumentEntity(ContentEntityDto dto)
        {
            // EntitySlim does not track changes
            var entity = new DocumentEntitySlim();
            BuildContentEntity(entity, dto);
            
            //fill in the invariant info
            entity.Edited = dto.Edited;
            entity.Published = dto.Published;

            var publishedCultures = new List<string>();
            var editedCultures = new List<string>();

            //fill in the variant info
            if (dto.Variations.VariesByCulture() && dto.VariationInfo != null && dto.VariationInfo.Count > 0)
            {
                //fixme: Currently require making a 2nd query to fill in publish status information for variants

                var variantInfo = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
                foreach (var info in dto.VariationInfo)
                {
                    var isoCode = _langRepository.GetIsoCodeById(info.LanguageId);
                    if (isoCode != null)
                        variantInfo[isoCode] = info.Name;
                    
                }
                entity.CultureNames = variantInfo;
                entity.Variations = dto.Variations;
                entity.PublishedCultures = publishedCultures;
                entity.EditedCultures = editedCultures;
            }
            return entity;
        }

        #endregion
    }
}
