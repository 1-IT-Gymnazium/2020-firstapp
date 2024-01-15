using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BluePartners.Base.DI;
using BluePartners.Effit.DTO;
using BluePartners.Effit.DTO.AccountModule;
using BluePartners.Effit.DTO.BusinessProcessModule;
using BluePartners.Effit.DTO.Common;
using BluePartners.Effit.DTO.ContractRegister;
using BluePartners.Effit.DTO.GdprRegister;
using BluePartners.Effit.DTO.RiskRegister;
using BluePartners.Effit.Entity;
using BluePartners.Effit.Services.GapModule.ViewModels;
using BluePartners.Effit.Services.GdprModule.ViewModels;
using BluePartners.Effit.Services.RiskModule;
using BluePartners.Effit.Services.InfrastructureModule.ViewModels;
using BluePartners.Effit.Services.Shared;
using BluePartners.Effit.Services.Shared.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using BluePartners.Effit.Services.RiskModule.ViewModels;
using AutoMapper;
using Microsoft.Extensions.Localization;

namespace BluePartners.Effit.Services.GapModule
{
    public class GapService : IGapService, IScopedService<IGapService>
    {
        private readonly IUnitOfWork unitOfWork;
        private readonly IUserService userService;
        private readonly IRiskAnalysisService riskAnalysisService;
        private readonly IRiskService riskService;
        private readonly IStringLocalizer<GapService> stringLocalizer;

        public GapService(
            IUnitOfWork unitOfWork,
            IUserService userService,
            IRiskAnalysisService riskAnalysisService,
            IRiskService riskService,
            IStringLocalizer<GapService> stringLocalizer
        )
        {
            this.unitOfWork = unitOfWork;
            this.userService = userService;
            this.riskAnalysisService = riskAnalysisService;
            this.riskService = riskService;
            this.stringLocalizer = stringLocalizer;
        }

        #region Analysis

        public async Task<GapAnalysisViewModel> CreateAnalysis(RiskAnalysis riskAnalysis, UserCompany userCompany)
        {
            var vm = new GapAnalysisViewModel
            {
                Id = riskAnalysis.Id,
                Name = riskAnalysis.Name,
                IsDefault = riskAnalysis.IsDefault,
            };

            var dataForColumns = new Dictionary<Tuple<string, string>, List<string>>();

            vm.riskCategories = await unitOfWork.GetRepository<Category>()
                                        .Query(x => x.Lifecycle_DeletedDate == null
                                               && x.CategoryTypeId == CategoryType.Entry.RecordImpact)
                                        .FilterAccessible(userCompany)
                                        .OrderBy(x => x.Value)
                                        .ToListAsync();

            var measureList = riskAnalysis.IsDefault
                ? new List<GdprSecurityMeasuresCategory.Entry>
                {
                    GdprSecurityMeasuresCategory.Entry.ContractMeasures,
                    GdprSecurityMeasuresCategory.Entry.SystemSla,
                    GdprSecurityMeasuresCategory.Entry.StorageSla,
                }
                : new List<GdprSecurityMeasuresCategory.Entry>
                {
                    GdprSecurityMeasuresCategory.Entry.ContractMeasures,
                    GdprSecurityMeasuresCategory.Entry.RecordMeasures,
                    GdprSecurityMeasuresCategory.Entry.NodeMeasures,
                    GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures,
                    GdprSecurityMeasuresCategory.Entry.SystemSla,
                    GdprSecurityMeasuresCategory.Entry.StorageSla,
                };

            var measuresType = await unitOfWork.GetRepository<GdprSecurityMeasuresCategory>()
                                                .Query(x => measureList.All(m => m != x.Id))
                                                .ToListAsync();

            var selectedList = await unitOfWork.GetRepository<GdprGAPCell>()
                                              .Query(x => x.RiskAnalysisId == riskAnalysis.Id)
                                              .FilterAccessible(userCompany)
                                              .Include(x => x.SelectedCategoryWithDeleted)
                                                  .ThenInclude(x => x.Category)
                                              .ToListAsync();

            if (riskAnalysis.IsDefault)
            {
                vm.contractMeasures = await unitOfWork.GetRepository<GdprGAPCategory>()
                .Query(x => x.UserCompanyId == userCompany.Id && x.CategoryTypeId == CategoryType.Entry.ContractType && x.RiskAnalysisId == riskAnalysis.Id)
                .Include(x => x.Category)
                .Select(x => x.Category.Name)
                .ToListAsync();
            }

            bool showValue;
            foreach (var cell in selectedList)
            {
                showValue = cell.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures
                            || cell.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.NodeMeasures
                            || cell.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures;

                Tuple<string, string> tmp = new Tuple<string, string>(cell.GdprSecurityMeasuresCategoryId.ToString(), cell.RiskCategoryId.ToString());
                if (dataForColumns.ContainsKey(tmp))
                {
                    continue;
                }
                dataForColumns.Add(tmp, cell.SelectedCategory.Select(x => showValue ? x.Category.NameWithValue : x.Category.Name).ToList());
            }

            foreach (var measureType in measuresType)
            {
                GapSelectBoxRow row = new GapSelectBoxRow { measureType = measureType.Name };
                foreach (var riskCategory in vm.riskCategories)
                {
                    var column = new GapSelectBoxColumn();
                    var dictKey = new Tuple<string, string>(measureType.Id.ToString(), riskCategory.Id.ToString());
                    if (dataForColumns.ContainsKey(dictKey))
                    {
                        column.selectedMeasures = dataForColumns[dictKey];
                    }
                    else
                    {
                        column.selectedMeasures = new List<string>();
                    }
                    column.riskCategoryId = riskCategory.Id.ToString();
                    column.measureType = measureType.Id;
                    row.columns.Add(column);
                }
                vm.rows.Add(row);
            }

            return vm;
        }

        public async Task UpdateGdprGapCell(ModelStateDictionary modelState, GdprGAPCell cellEntity, GapSelectBoxColumn selectBoxVm)
        {
            var selectedPrev = cellEntity.SelectedCategory.Select(x => x.Category.Id.ToString()).ToList();
            var selectedNow = selectBoxVm.selectedMeasures;
            var strategy = DatabaseUpdateStrategy.Create(selectedPrev, selectedNow);
            var user = await userService.GetCurrentUserAsync();

            //Hledani spravneho typu kategorie
            CategoryType.Entry catType;
            switch (selectBoxVm.measureType)
            {
                case GdprSecurityMeasuresCategory.Entry.RecordMeasures:
                    catType = CategoryType.Entry.GdprOrganizationalMeasures;
                    break;

                case GdprSecurityMeasuresCategory.Entry.SystemMeasures:
                    catType = CategoryType.Entry.GdprSystemTechnicalSecurity;
                    break;

                case GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures:
                    catType = CategoryType.Entry.GdprStorageTechnicalSecurity;
                    break;

                case GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures:
                case GdprSecurityMeasuresCategory.Entry.NodeMeasures:
                case GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures:
                    catType = CategoryType.Entry.PhysicalSecurity;
                    break;

                default:
                    catType = CategoryType.Entry.PhysicalSecurity;
                    break;
            }

            foreach (var key in strategy.KeysToRemove)
            {
                cellEntity.SelectedCategory.Single(x => x.Category.Id.ToString() == key).DeletedByUser(user);
            }

            foreach (var key in strategy.KeysToAdd)
            {
                var cat = await unitOfWork.GetRepository<Category>()
                                          .Query(x => x.Id.ToString() == key
                                                 && x.CategoryTypeId == catType)
                                          .FilterAccessible(await userService.GetCurrentUserCompanyAsync())
                                          .SingleOrDefaultAsync();
                // Kontrola, že kategorie existuje
                if (cat == null)
                {
                    continue;
                }
                var selectedEntity = new GdprGAPSelectedItem
                {
                    Id = Guid.NewGuid(),
                    GdprGAPCellId = cellEntity.Id,
                    CategoryId = cat.Id,
                };

                cellEntity.SelectedCategoryWithDeleted.Add(selectedEntity.CreatedByUser(user));
                unitOfWork.AddForInsert(selectedEntity);
            }

            cellEntity.ModifiedByUser(await userService.GetCurrentUserAsync());
        }

        public async Task SaveContractTypeCell(ModelStateDictionary modelState, GapSelectBoxColumn selectBoxVm)
        {
            var userCompany = await userService.GetCurrentUserCompanyAsync();
            var user = await userService.GetCurrentUserAsync();

            var current = await unitOfWork.GetRepository<GdprGAPCategory>()
                .Query(x => x.UserCompanyId == userCompany.Id && x.CategoryTypeId == CategoryType.Entry.ContractType).Include(x => x.Category).ToListAsync();
            var newItems = selectBoxVm.selectedMeasures;
            var strategy = DatabaseUpdateStrategy.Create(current.Select(x => x.Category.Name), newItems);

            foreach (var key in strategy.KeysToRemove)
            {
                current.Single(x => x.Category.Name == key).DeletedByUser(user);
            }

            foreach (var key in strategy.KeysToAdd)
            {
                var cat = await unitOfWork.GetRepository<Category>()
                    .Query(x => x.Name == key
                                && x.CategoryTypeId == CategoryType.Entry.ContractType)
                    .FilterAccessible(userCompany)
                    .SingleAsync();

                var selectedEntity = new GdprGAPCategory()
                {
                    Id = Guid.NewGuid(),
                    RiskAnalysisId = selectBoxVm.Id,
                    CategoryTypeId = CategoryType.Entry.ContractType,
                    CategoryId = cat.Id,
                    UserCompanyId = userCompany.Id,
                }.CreatedByUser(user);

                //cellEntity.SelectedCategoryWithDeleted.Add(selectedEntity.CreatedByUser(user));
                unitOfWork.AddForInsert(selectedEntity);
            }
        }

        #endregion/

        #region RiskMerge

        public async Task<GapRiskManagementViewModel> GetGapRiskRows(GapRiskManagementRequestModel model)
        {
            var vm = new GapRiskManagementViewModel
            {
                Rows = new List<GapRiskManagementRowViewModel>(),
                Request = model,
            };

            if (!model.Statuses.Any() || !model.Analyses.Any())
            {
                return vm;
            }

            var userCompany = await userService.GetCurrentUserCompanyAsync();
            var selectedGapAnalyses = new List<RiskAnalysis>();
            foreach (var analysisId in model.Analyses)
            {
                selectedGapAnalyses.Add(riskAnalysisService.Find(userCompany, analysisId));
            }

            var allGapAnalyses = riskAnalysisService.FindList(userCompany, true);

            var allGapRiskList = await unitOfWork.GetRepository<GapRisk>()
                .Query(x => x.RiskAnalysis.UserCompanyId == userCompany.Id)
                .Include(x => x.RiskAnalysis)
                .ToListAsync();

            var allRisksList = await unitOfWork.GetRepository<Risk>()
                .Query(x => allGapRiskList.Any(y => x.Id == y.RiskId))
                .FilterAccessible(userCompany)
                .Include(x => x.GapRisksWithDeleted)
                .ToListAsync();

            var gapRiskItems = new List<GapRiskItem>();
            var stateList = CreateStateListFromModel(model);
            await UpdateGapRisksForSelectedAnalyses(stateList, selectedGapAnalyses, allGapAnalyses, allRisksList, gapRiskItems);

            var currentRisks = allRisksList.Where(x => selectedGapAnalyses.Any(r => x.GapRisks.Any(g => r.Id == g.RiskAnalysisId)));
            // Import současných rizik do view modelu
            var gapRiskManagementRows = ConvertRisksToGapRiskManagementRows(currentRisks);
            vm.Rows.AddRange(gapRiskManagementRows);

            vm = await AddGapRisksToViewModel(vm, gapRiskItems, currentRisks, selectedGapAnalyses);

            return vm;
        }
        /// <summary>
        /// Metoda přidává k existujícím řádkům s riziky gapRisky. To buď s akcí update, pokud narazí na riziko,
        /// nebo s akcí Přidat, pokud k danému gapRisku ještě riziko neexistuje
        /// </summary>
        /// <param name="vm">ViewModel</param>
        /// <param name="gapRiskItems">Všechny gapRisky</param>
        /// <param name="currentRisks">Aktuální rizika</param>
        /// <param name="selectedGapAnalyses">Vybrané rizikové analýzy</param>
        /// <returns>View model GapRiskManagementViewModel</returns>
        private async Task<GapRiskManagementViewModel> AddGapRisksToViewModel(
            GapRiskManagementViewModel vm,
            List<GapRiskItem> gapRiskItems,
            IEnumerable<Risk> currentRisks,
            List<RiskAnalysis> selectedGapAnalyses)
        {
            var riskLevels = unitOfWork.GetRepository<Category>()
                .Query(x => x.CategoryTypeId == CategoryType.Entry.RiskLevel)
                .FilterAccessible(await userService.GetCurrentUserCompanyAsync())
                .OrderByDescending(x => x.Value)
                .ToList();

            foreach (var gapItem in gapRiskItems)
            {
                var maxRiskLevel = gapItem.ContentByRa.Select(x => x.Value.RiskLevel).Max(x => x);
                var riskLevelCategory = riskLevels.FindIntervalForLevel(maxRiskLevel).Category;
                var proposedRisk = new RiskToCompareViewModel
                {
                    riskSourceId = gapItem.ObjectId.ToString().ToLowerInvariant(),
                    riskName = gapItem.GapRiskTemplate.Name,
                    riskSourceTypeId = GetThreatSource(gapItem.Type),
                    riskLevel = Mapper.Map<CategorySimpleViewModel>(riskLevelCategory),
                    riskProposedMeasure = gapItem.GapRiskTemplate.ProposedMeasures,
                    riskDescription = gapItem.GapRiskTemplate.Description,
                };

                foreach (var raDictRow in gapItem.ContentByRa)
                {
                    var gapAnalysis = selectedGapAnalyses.FirstOrDefault(x => x.Id == raDictRow.Key);
                    if (gapAnalysis != null)
                    {
                        var gapRiskLevelCategory = riskLevels.FindIntervalForLevel(raDictRow.Value.RiskLevel).Category;
                        proposedRisk.gapRisks.Add(new GapRiskToCompareViewModel
                        {
                            riskAnalysisId = gapAnalysis.Id.ToString().ToLowerInvariant(),
                            riskAnalysisName = gapAnalysis.DisplayName,
                            riskLevelId = gapRiskLevelCategory.Id.ToString().ToLowerInvariant(),
                            riskLevelValue = gapRiskLevelCategory.Value.Value,
                            securityMeasureId = (int)gapItem.Type,
                            description = GetGapRiskDescriptionModel(gapItem.Type, new List<GapRiskItemContent> { raDictRow.Value }),
                        });
                    }
                }

                var riskToCompare = currentRisks.SingleOrDefault(x => x.GapRisks.Any(y => y.CategoryId == gapItem.Type) &&
                            ((x.SourceId == ThreatSource.Entry.System && x.InfoSystemId == gapItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Storage && x.StorageId == gapItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Location && x.FileServiceNodeId == gapItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Record && x.RecordId == gapItem.ObjectId)
                            ));

                if (!(riskToCompare is null))
                {
                    var riskToCompareId = riskToCompare.Id.ToString().ToLowerInvariant();
                    var toEdit = vm.Rows.Single(x => !(x.currentRisk is null) && x.currentRisk.riskId == riskToCompareId);
                    // Set RiskId of GapRisk records
                    proposedRisk.gapRisks.ForEach(x => { x.riskId = riskToCompareId; });
                    toEdit.proposedRisk = proposedRisk;
                    toEdit.action = GapServiceHelpers.Action.Update;
                }
                else
                {
                    vm.Rows.Add(new GapRiskManagementRowViewModel
                    {
                        proposedRisk = proposedRisk,
                        action = GapServiceHelpers.Action.Add,
                    });
                }
            }

            return vm;
        }

        /// <summary>
        /// Konvertuje aktuální rizika do viewmodelu s akcí Remove (vytváří řádky viewmodelu).
        /// Druhá metoda následně pokud napáruje GapRisk, změní akci na Update.
        /// </summary>
        /// <param name="currentRisks">Aktuální rizika</param>
        /// <returns>Seznam GapRiskManagementRow view modelů</returns>
        private List<GapRiskManagementRowViewModel> ConvertRisksToGapRiskManagementRows(IEnumerable<Risk> currentRisks)
        {
            var result = new List<GapRiskManagementRowViewModel>();
            foreach (var risk in currentRisks)
            {
                var sBuilder = new StringBuilder();
                sBuilder.AppendLine("<span><b>" + stringLocalizer.GetString("Popis") + ":</b> " + risk.Description + "</span>");
                sBuilder.AppendLine("<span><b>" + stringLocalizer.GetString("Opatření") + ":</b> " + risk.ProposedMinimizationMeasures + "</span>");
                sBuilder.AppendLine(risk.Note);
                sBuilder.Replace("\r\n", "\r");
                sBuilder.Replace("\n", "\r");
                sBuilder.Replace("\r", "<br />");
                sBuilder.Replace("\t", " &emsp;");
                sBuilder.Replace("  ", " &nbsp;");
                var riskDescription = sBuilder.ToString();

                var currentRisk = new RiskToCompareViewModel
                {
                    riskDescription = riskDescription,
                    riskId = risk.Id.ToString().ToLowerInvariant(),
                    riskLevel = Mapper.Map<CategorySimpleViewModel>(risk.RiskLevel),
                    riskName = risk.Name,
                    riskSourceId = risk.SourceId.ToString(),
                    riskSourceTypeId = (int)risk.SourceId,
                };

                var gapRiskManagementRow = new GapRiskManagementRowViewModel
                {
                    action = GapServiceHelpers.Action.Remove,
                    proposedRisk = null,
                    currentRisk = currentRisk,
                };

                result.Add(gapRiskManagementRow);
            }

            return result;
        }

        private static List<int> CreateStateListFromModel(GapRiskManagementRequestModel model)
        {
            var stateList = new List<int>();
            foreach (var state in model.Statuses)
            {
                if (state != nameof(GdprRecordState.Entry.Inactive))
                {
                    switch (state)
                    {
                        case nameof(GdprRecordState.Entry.Active):
                            stateList.Add((int)GdprRecordState.Entry.Active);
                            break;
                        case nameof(GdprRecordState.Entry.Draft):
                            stateList.Add((int)GdprRecordState.Entry.Draft);
                            break;
                        case nameof(GdprRecordState.Entry.Waiting):
                            stateList.Add((int)GdprRecordState.Entry.Waiting);
                            break;
                    }
                }
            }

            return stateList;
        }

        private async Task UpdateGapRisksForSelectedAnalyses(List<int> stateList, List<RiskAnalysis> selectedGapAnalyses, IEnumerable<RiskAnalysis> allGapAnalyses, List<Risk> allRisksList, List<GapRiskItem> gapRiskItems)
        {
            foreach (var gapAnalysis in allGapAnalyses)
            {
                // podmínka pro vybrané gapky
                if (selectedGapAnalyses.Any(x => x.Id == gapAnalysis.Id))
                {
                    if (gapAnalysis.IsDefault)
                    {
                        await CreateReportRecordData(gapAnalysis, stateList, gapRiskItems);
                    }
                    else
                    {
                        await CreateReportProcessData(gapAnalysis, gapRiskItems);
                    }
                }
                else
                {
                    foreach (var gapRiskItem in gapRiskItems)
                    {
                        foreach (var ra in allGapAnalyses)
                        {
                            var risk = allRisksList.SingleOrDefault(x => x.GapRisksWithDeleted.Any(y => y.Lifecycle_DeletedDate != null && y.RiskAnalysisId == ra.Id && y.CategoryId == gapRiskItem.Type)
                            && ((x.SourceId == ThreatSource.Entry.System && x.InfoSystemId == gapRiskItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Storage && x.StorageId == gapRiskItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Location && x.FileServiceNodeId == gapRiskItem.ObjectId)
                            || (x.SourceId == ThreatSource.Entry.Record && x.RecordId == gapRiskItem.ObjectId))
                            );

                            if (!(risk is null))
                            {
                                //var gapRisk = risk.GapRisks.Single(x => x.RiskAnalysisId == gapAnalysis.Id && x.CategoryId == gapRiskItem.Type);
                                if (!gapRiskItem.ContentByRa.ContainsKey(ra.Id))
                                {
                                    gapRiskItem.ContentByRa.Add(ra.Id, new GapRiskItemContent
                                    {
                                        Active = false,
                                        Description = risk.Description,
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }

        public async Task ProcessGapRisks(GapRiskManagementViewModel vm, ModelStateDictionary modelState)
        {
            foreach (var row in vm.Rows)
            {
                switch (row.action)
                {
                    case GapServiceHelpers.Action.Add:
                        await RiskCreate(modelState, row.proposedRisk);
                        break;
                    case GapServiceHelpers.Action.Remove:
                        await RiskDelete(modelState, row.currentRisk, vm.Request.Analyses);
                        break;
                    case GapServiceHelpers.Action.Update:
                        await RiskUpdate(modelState, row.currentRisk, row.proposedRisk);
                        break;
                }
            }

            await unitOfWork.SaveChangesAsync();
        }

        private async Task RiskCreate(ModelStateDictionary modelState, RiskToCompareViewModel proposedRisk)
        {
            var riskToCreate = new RiskViewModel
            {
                name = proposedRisk.riskName,
                description = proposedRisk.riskDescription,
                proposedMinimizationMeasures = proposedRisk.riskProposedMeasure,
                riskLevelId = proposedRisk.riskLevel.id,
                riskLevelName = proposedRisk.riskLevel.name,
                threatSourceId = proposedRisk.riskSourceTypeId,
                riskStatusName = RiskStatus.Entry.Active.EnumDisplayNameFor(),
                // Rizika vzniklá z GAP analýzy nemohou mít vyplněnou hodnotu Dopad na úroveň rizika!
                riskLevelImpactId = null,
                riskLevelImpactName = null,
            };

            if (proposedRisk.gapRisks.Any())
            {
                riskToCreate.note = CreateNoteFromGapRisksDescription(proposedRisk.gapRisks);
            }
            if (proposedRisk.riskSourceTypeId == (int)ThreatSource.Entry.System)
            {
                riskToCreate.informationSystemId = proposedRisk.riskSourceId;
            }
            else if (proposedRisk.riskSourceTypeId == (int)ThreatSource.Entry.Storage)
            {
                riskToCreate.storageId = proposedRisk.riskSourceId;
            }
            else if (proposedRisk.riskSourceTypeId == (int)ThreatSource.Entry.Location)
            {
                riskToCreate.fileServiceNodeId = proposedRisk.riskSourceId;
            }
            else
            {
                riskToCreate.recordId = proposedRisk.riskSourceId;
            }
            var createdRisk = await riskService.CreateRisk(modelState, riskToCreate);
            await SaveGapRisks(createdRisk, proposedRisk, true);
        }

        private async Task RiskDelete(ModelStateDictionary modelState, RiskToCompareViewModel risk, IEnumerable<Guid> raIds)
        {
            if (Guid.TryParse(risk.riskId, out var riskId))
            {
                var appUser = await userService.GetCurrentUserAsync();
                var gapRisks = await unitOfWork.GetRepository<GapRisk>().Query(x => x.RiskId == riskId).Include(x => x.RiskLevel).ToListAsync();
                var selectedGapRisks = gapRisks.Where(x => raIds.Any(ra => ra == x.RiskAnalysisId)).ToList();

                foreach (var item in selectedGapRisks)
                {
                    item.DeletedByUser(appUser);
                }

                var type = gapRisks.First().CategoryId;

                var gapContents = gapRisks.Except(selectedGapRisks).Select(x => new GapRiskItemContent
                {
                    Active = false,
                    Description = x.Description,
                    RiskLevel = x.RiskLevel.Value.Value,
                }).ToList();

                if (gapContents.Any())
                {
                    var proposedRisk = new RiskToCompareViewModel
                    {
                        riskId = risk.riskId,
                        riskName = risk.riskName,
                        riskSourceId = risk.riskSourceId,
                        riskSourceTypeId = risk.riskSourceTypeId,
                        riskProposedMeasure = risk.riskProposedMeasure,
                        //riskDescription = GetGapRiskDescriptionModel(type, gapContents),
                        riskLevel = new CategorySimpleViewModel
                        {
                            value = gapContents.Max(x => x.RiskLevel),
                        },
                    };
                    await RiskUpdate(modelState, risk, proposedRisk);
                }
                else
                {
                    await riskService.DeleteRisk(riskId);
                }
            }
        }

        private async Task RiskUpdate(ModelStateDictionary modelState, RiskToCompareViewModel currentRisk, RiskToCompareViewModel proposedRisk)
        {
            if (Guid.TryParse(currentRisk.riskId, out var guid))
            {
                var vmRisk = await riskService.CreateModelForEdit(guid);
                var dbRisk = await riskAnalysisService.FindAndPrepareRisk(guid);

                vmRisk.description = proposedRisk.riskDescription;
                if (proposedRisk.gapRisks.Any())
                {
                    vmRisk.note = CreateNoteFromGapRisksDescription(proposedRisk.gapRisks);
                }

                vmRisk.riskLevelId = proposedRisk.riskLevel.id;
                vmRisk.riskLevelName = proposedRisk.riskLevel.name;
                vmRisk.proposedMinimizationMeasures = proposedRisk.riskProposedMeasure;
                // Rizika vzniklá z GAP analýzy nemohou mít vyplněnou hodnotu Dopad na úroveň rizika!
                vmRisk.riskLevelImpactId = null;
                vmRisk.riskLevelImpactName = null;

                await riskService.UpdateRisk(modelState, dbRisk, vmRisk);
                await SaveGapRisks(dbRisk, proposedRisk);
            }
        }

#pragma warning disable CA1304 // Specify StringComparison - not on IQueryable
#pragma warning disable CA1305 // Specify IFormatProvider
        private async Task SaveGapRisks(Risk dbRisk, RiskToCompareViewModel proposedRisk, bool isCreateAction = false)
        {
            var appUser = await userService.GetCurrentUserAsync();
            foreach (var proposedGapRisk in proposedRisk.gapRisks)
            {
                GapRisk gapRisk = null;
                var riskAnalysisId = Guid.Parse(proposedGapRisk.riskAnalysisId);
                // Vyhledání existujícího záznamu GapRisk má smysl pouze u aktualizace Risku
                if (!isCreateAction)
                {
                    gapRisk = await unitOfWork.GetRepository<GapRisk>()
                        .Query(x => x.RiskId == dbRisk.Id && x.RiskAnalysisId == riskAnalysisId)
                        .SingleOrDefaultAsync();
                }

                if (gapRisk is null)
                {
                    unitOfWork.AddForInsert(new GapRisk
                    {
                        CategoryId = (GdprSecurityMeasuresCategory.Entry)proposedGapRisk.securityMeasureId,
                        Description = CreateNoteFromGapRisksDescription(new List<GapRiskToCompareViewModel>
                        {
                            proposedGapRisk,
                        }),
                        RiskLevelId = Guid.Parse(proposedGapRisk.riskLevelId),
                        RiskAnalysisId = riskAnalysisId,
                        Risk = dbRisk,
                    }.CreatedByUser(appUser));
                }
                else
                {
                    gapRisk.Description = CreateNoteFromGapRisksDescription(new List<GapRiskToCompareViewModel>
                    {
                        proposedGapRisk,
                    });
                    gapRisk.RiskLevelId = Guid.Parse(proposedGapRisk.riskLevelId);
                    gapRisk.ModifiedByUser(appUser);
                }
            }
            // Smazání GapRisk položek, které již nejsou platné
            if (!isCreateAction)
            {
                var gapRisks = await unitOfWork.GetRepository<GapRisk>().Query(x => x.RiskId == dbRisk.Id).ToListAsync();
                foreach (var gapRisk in gapRisks)
                {
                    if (!proposedRisk.gapRisks.Any(x => x.riskAnalysisId == gapRisk.RiskAnalysisId.ToString().ToLowerInvariant()))
                    {
                        // Pokud je GapRisk záznam navázaný na RiskAnalysis, která se již nevyskytuje v návrhu rizika, smazat!
                        gapRisk.DeletedByUser(appUser);
                    }
                }
            }
        }
#pragma warning restore CA1305 // Specify IFormatProvider
#pragma warning restore CA1304 // Specify StringComparison

        private string CreateNoteFromGapRisksDescription(List<GapRiskToCompareViewModel> gapRisks)
        {
            var sBuilder = new StringBuilder();
            foreach (var gapRisk in gapRisks)
            {
                sBuilder.AppendLine("\t" + gapRisk.riskAnalysisName);
                switch (gapRisk.securityMeasureId)
                {
                    case (int)GdprSecurityMeasuresCategory.Entry.ContractMeasures:
                    case (int)GdprSecurityMeasuresCategory.Entry.RecordMeasures:
                        break;
                    case (int)GdprSecurityMeasuresCategory.Entry.StorageSla:
                    case (int)GdprSecurityMeasuresCategory.Entry.SystemSla:
                        sBuilder.AppendLine(gapRisk.description.missingMeasures);
                        break;
                    case (int)GdprSecurityMeasuresCategory.Entry.SystemMeasures:
                    case (int)GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures:
                        if (!string.IsNullOrEmpty(gapRisk.description.requiredMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.AppendLine(stringLocalizer.GetString("Požadované technické zabezpečení") + ":");
                            var requiredMeasures = gapRisk.description.requiredMeasures.Split(',');
                            foreach (var requiredMeasure in requiredMeasures)
                            {
                                sBuilder.AppendLine("\t\t\t" + requiredMeasure.Trim());
                            }
                        }

                        if (!string.IsNullOrEmpty(gapRisk.description.currentMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.AppendLine(stringLocalizer.GetString("Stávající technické zabezpečení") + ":");
                            var currentMeasures = gapRisk.description.currentMeasures.Split(',');
                            foreach (var currentMeasure in currentMeasures)
                            {
                                sBuilder.AppendLine("\t\t\t" + currentMeasure.Trim());
                            }
                        }

                        if (!string.IsNullOrEmpty(gapRisk.description.missingMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.AppendLine(stringLocalizer.GetString("Chybějící technické zabezpečení") + ":");
                            var missingMeasures = gapRisk.description.missingMeasures.Split(',');
                            foreach (var missingMeasure in missingMeasures)
                            {
                                sBuilder.AppendLine("\t\t\t" + missingMeasure.Trim());
                            }
                        }
                        break;
                    default:
                        if (!string.IsNullOrEmpty(gapRisk.description.requiredMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.Append(stringLocalizer.GetString("Požadované fyzické zabezpečení") + ": ");
                            sBuilder.AppendLine(gapRisk.description.requiredMeasures.Trim());
                        }

                        if (!string.IsNullOrEmpty(gapRisk.description.currentMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.Append(stringLocalizer.GetString("Stávající fyzické zabezpečení") + ": ");
                            sBuilder.AppendLine(gapRisk.description.currentMeasures.Trim());
                        }

                        if (!string.IsNullOrEmpty(gapRisk.description.missingMeasures))
                        {
                            sBuilder.Append("\t\t");
                            sBuilder.Append(stringLocalizer.GetString("Chybějící fyzické zabezpečení") + ": ");
                            sBuilder.AppendLine(gapRisk.description.missingMeasures.Trim());
                        }
                        break;
                }
            }

            return sBuilder.ToString();
        }

        private GapRiskItem CreateRiskTemplateItem(Guid id, string name, GdprSecurityMeasuresCategory.Entry type, Category impact, Guid raId, Tuple<int, int> currentMinMax, Tuple<int, int> newMinMax)
        {
            var item = new GapRiskItem
            {
                ObjectId = id,
                ObjectName = name,
                Type = type,
                GapRiskTemplate = GetRiskTemplate(type),
            };
            item.GapRiskTemplate.Name = item.GapRiskTemplate.Name.Replace("%param%", item.ObjectName, StringComparison.CurrentCulture);
            item.ContentByRa.Add(
                raId,
                new GapRiskItemContent
                {
                    RiskLevel = (int)(GetRelativeGapRiskLevelValue(impact.Value.Value, currentMinMax, newMinMax) * item.GapRiskTemplate.Coefficient),
                    Description = item.GapRiskTemplate.Description,
                });
            return item;
        }

        private static int GetThreatSource(GdprSecurityMeasuresCategory.Entry type)
        {
            switch (type)
            {
                case GdprSecurityMeasuresCategory.Entry.ContractMeasures:
                case GdprSecurityMeasuresCategory.Entry.RecordMeasures:
                    return (int)ThreatSource.Entry.Record;
                case GdprSecurityMeasuresCategory.Entry.StorageSla:
                case GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures:
                case GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures:
                    return (int)ThreatSource.Entry.Storage;
                case GdprSecurityMeasuresCategory.Entry.SystemSla:
                case GdprSecurityMeasuresCategory.Entry.SystemMeasures:
                    return (int)ThreatSource.Entry.System;
                default:
                    return (int)ThreatSource.Entry.Location;
            }
        }

        private static GapRiskToCompareDescriptionViewModel GetGapRiskDescriptionModel(GdprSecurityMeasuresCategory.Entry type, List<GapRiskItemContent> content)
        {
            var result = new GapRiskToCompareDescriptionViewModel();
            foreach (var c in content.Where(x => x.Active))
            {
                switch (type)
                {
                    case GdprSecurityMeasuresCategory.Entry.ContractMeasures:
                    case GdprSecurityMeasuresCategory.Entry.RecordMeasures:
                        break;
                    case GdprSecurityMeasuresCategory.Entry.StorageSla:
                    case GdprSecurityMeasuresCategory.Entry.SystemSla:
                        result.missingMeasures = string.Join(", ", c.MissingMeasures);
                        break;
                    case GdprSecurityMeasuresCategory.Entry.SystemMeasures:
                    case GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures:
                        result.missingMeasures = string.Join(", ", c.MissingMeasures);
                        result.currentMeasures = string.Join(", ", c.CurrentMeasures);
                        result.requiredMeasures = string.Join(", ", c.RequiredMeasures);
                        break;
                    default:
                        result.missingMeasures = c.MissingValue.ToString(CultureInfo.CurrentCulture);
                        result.currentMeasures = c.CurrentValue.ToString(CultureInfo.CurrentCulture);
                        result.requiredMeasures = c.RequiredValue.ToString(CultureInfo.CurrentCulture);
                        break;
                }
            }
            return result;
        }

        private GapRiskTemplate GetRiskTemplate(GdprSecurityMeasuresCategory.Entry type)
        {
            switch (type)
            {
                case GdprSecurityMeasuresCategory.Entry.RecordMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Chybí organizační opatření %param%",
                        Description = "Neexistuje vnitřní předpis upravující práva a povinnosti uživatelů.",
                        ProposedMeasures = "Vydejte vnitřní předpis ve kterém jsou popsána příslušná pravidla a povinnosti.",
                        Coefficient = 0.5,
                    };
                case GdprSecurityMeasuresCategory.Entry.SystemMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Systém %param% je nedostatečně technicky zabezpečen",
                        Description = "Systém je nedostatečně technicky zabezpečen. Může dojít k bezpečnostnímu incidentu.",
                        ProposedMeasures = "Zajistěte adekvátní technické zabezpečení.",
                        Coefficient = 1,
                    };
                case GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Úložiště %param% je nedostatečně technicky zabezpečeno",
                        Description = "Úložiště je nedostatečně technicky zabezpečeno. Může dojít k bezpečnostnímu incidentu.",
                        ProposedMeasures = "Zajistěte adekvátní technické zabezpečení.",
                        Coefficient = 1,
                    };
                case GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Úložiště %param% je nedostatečně fyzicky zabezpečeno",
                        Description = "Úložiště je nedostatečně zabezpečeno proti fyzické manipulaci s daty. Může dojít k bezpečnostnímu incidentu.",
                        ProposedMeasures = "Zajistěte adekvátní fyzické zabezpečení.",
                        Coefficient = 0.75,
                    };
                case GdprSecurityMeasuresCategory.Entry.NodeMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Místo %param% je nedostatečně fyzicky zabezpečeno",
                        Description = "Místo je nedostatečně zabezpečeno proti fyzické manipulaci s dokumenty. Může dojít k bezpečnostnímu incidentu.",
                        ProposedMeasures = "Zajistěte adekvátní fyzické zabezpečení místa.",
                        Coefficient = 0.5,
                    };
                case GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Místo %param% je nedostatečně fyzicky zabezpečeno",
                        Description = "Místo je nedostatečně zabezpečeno proti fyzické manipulaci s dokumenty. Může dojít k bezpečnostnímu incidentu.",
                        ProposedMeasures = "Zajistěte adekvátní fyzické zabezpečení místa.",
                        Coefficient = 0.5,
                    };
                case GdprSecurityMeasuresCategory.Entry.ContractMeasures:
                    return new GapRiskTemplate
                    {
                        Name = "Se zpracovatelem %param% není uzavřena příslušná smlouva",
                        Description = "Nemáte uzavřenou příslušnou zpracovatelskou smlouvu. Nejste v souladu s Nařízením GDPR.",
                        ProposedMeasures = "Zajistěte uzavření smlouvy se zpracovatelem v souladu s GDPR.",
                        Coefficient = 0.5,
                    };
                case GdprSecurityMeasuresCategory.Entry.SystemSla:
                    return new GapRiskTemplate
                    {
                        Name = "Pro systém %param% není uzavřena příslušná aktuálně platná SLA smlouva.",
                        Description = "Nemáte uzavřenou příslušnou SLA smlouvu. Nemáte garantovanou úroveň služeb u úložiště od dodavatelů ",
                        ProposedMeasures = "Zajistěte uzavření SLA smlouvy s dodavatelem.",
                        Coefficient = 1,
                    };
                case GdprSecurityMeasuresCategory.Entry.StorageSla:
                    return new GapRiskTemplate
                    {
                        Name = "Pro úložiště %param% není uzavřena příslušná aktuálně platná SLA smlouva.",
                        Description = "Nemáte uzavřenou příslušnou SLA smlouvu. Nemáte garantovanou úroveň služeb u úložiště od dodavatelů ",
                        ProposedMeasures = "Zajistěte uzavření SLA smlouvy s dodavatelem.",
                        Coefficient = 1,
                    };
                default:
                    return new GapRiskTemplate();
            }
        }

        private static Tuple<int, int> GetMinMaxValueFromCategories(IEnumerable<Category> categories)
        {
            return new Tuple<int, int>(categories.Where(x => x != null).Min(x => x.Value) ?? 0, categories.Where(x => x != null).Max(x => x.Value) ?? 0);
        }

        private static int GetRelativeGapRiskLevelValue(int val, Tuple<int, int> currentMinMax, Tuple<int, int> newMinMax)
        {
            return (newMinMax.Item2 - newMinMax.Item1) * (val / (currentMinMax.Item2 - currentMinMax.Item1));
        }

        #endregion

        #region Report

        public async Task<GapReportViewModelNew> CreateReportProcessData(RiskAnalysis riskAnalysis, List<GapRiskItem> riskTemplates)
        {
            var userCompany = await userService.GetCurrentUserCompanyAsync();
            var vm = new GapReportViewModelNew
            {
                ReportBase =
                {
                    ReportName = ReportNames.GapAnalysis,
                    CompanyName = userCompany.CompanyName,
                },
                withoutErrors = true, // Explicitní nastavení
            };

            // Kategorie dopadů
            var impactCategories = await unitOfWork.GetRepository<Category>()
                .Query(x => x.CategoryTypeId == CategoryType.Entry.RecordImpact)
                .FilterAccessible(userCompany)
                .OrderBy(x => x.Value)
                .ToListAsync();

            // Kategorie Rizikovost
            var riskCategories = await unitOfWork.GetRepository<Category>()
                .Query(x => x.CategoryTypeId == CategoryType.Entry.RiskLevel)
                .FilterAccessible(userCompany)
                .OrderBy(x => x.Value)
                .ToListAsync();

            // potřebujeme pro převod škál při vytváření rizika. Z dopadu musíme udělat rizikovost
            // všechny čtyři hodnoty se vyskytují ve vzorci pro převod
            var impactMinMax = GetMinMaxValueFromCategories(impactCategories);
            var riskLevelMinMax = GetMinMaxValueFromCategories(riskCategories);

            foreach (var g in impactCategories.GroupBy(x => x.Value))
            {
                if (g.Count() > 1)
                {
                    vm.errorMsg = "Něco je špatně s Vašimi číselníky Hodnocení dopadů, Dostupnost, Důvěrnost, Integrita. Report není v pořádku.";
                    vm.withoutErrors = false;
                    return vm;
                }

                vm.colors.Add(g.First().Value ?? 0, "background-color: " + g.First().Color);
                vm.riskNames.Add(g.First().Value ?? 0, g.First().Name);
            }

            vm.riskNamesArray = vm.riskNames.Select(x => x.Value).ToArray();

            // GAP ANALÝZA
            // Získávání dat do Reportu z MATICE RIZIK
            // Jednotlivé buňky GAP tabulky
            // Seřazeno dle Sledovaných kategorií
            var measuresForRisks = await unitOfWork.GetRepository<GdprGAPCell>()
                .Query(x => x.RiskAnalysisId == riskAnalysis.Id)
                .Include(x => x.GdprSecurityMeasuresCategory)
                .Include(x => x.SelectedCategoryWithDeleted)
                    .ThenInclude(x => x.Category)
                .Include(x => x.RiskCategory)
                .FilterAccessible(userCompany)
                .OrderBy(x => x.GdprSecurityMeasuresCategoryId)
                .ToListAsync();

            var measureList = new List<GdprSecurityMeasuresCategory.Entry>
                {
                    GdprSecurityMeasuresCategory.Entry.RecordMeasures,
                    GdprSecurityMeasuresCategory.Entry.NodeMeasures,
                    GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures,
                    GdprSecurityMeasuresCategory.Entry.ContractMeasures,
                };
            vm.measureNames.AddRange(await unitOfWork.GetRepository<GdprSecurityMeasuresCategory>()
                .Query(x => measureList.All(m => m != x.Id))
                .Select(x => x.Name)
                .ToListAsync());

            /* všechny recordy/procesy */
            var globalItems = await unitOfWork.GetRepository<BusinessProcess>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .OrderBy(x => x.AreaCategory.Order)
                    .ThenBy(x => x.Name)
                .ToListAsync();

            var globalSystems = await unitOfWork.GetRepository<GdprInfoSystem>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var globalStorages = await unitOfWork.GetRepository<GdprStorage>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var globalNodes = await unitOfWork.GetRepository<GdprFileServiceNode>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var measureCount = vm.measureNames.Count;

            // Procházení jednotlivých recordů
            foreach (var item in globalItems)
            {
                var iEval = item.Evaluations.SingleOrDefault(x => x.RiskAnalysisId == riskAnalysis.Id);
                var itemRow = new GapReportRecordViewModel
                {
                    Id = item.Id.ToString().ToLowerInvariant(),
                    Area = item.AreaCategory.Name,
                    Name = item.Name,
                };

                var gapRowExists = vm.rows.Any(x => x.recordName == item.Name);
                var gapRow = vm.rows.SingleOrDefault(x => x.recordName == item.Name) ?? new GapReportRowViewModel
                {
                    recordId = item.Id,
                    recordName = item.Name,
                    recordAreaId = item.AreaCategoryId,
                    recordAreaName = item.AreaCategory.Name,
                    recordAreaOrder = item.AreaCategory.Order,
                    missingMeasures = new int[measureCount],
                };

                if (IsRelevant(iEval))
                {
                    var currentItemImpact = ReturnImpact(new List<Category> { iEval.Availability, iEval.Confidentiality, iEval.Integrity }, impactCategories);
                    itemRow.HighestRisk = currentItemImpact.Value.Value;
                    gapRow.highestRisk = currentItemImpact.Value.Value;
                    gapRow.isRelevant = true;

                    #region SystemMeasures and StorageMeasures
                    if (item.Systems.Any(x => !x.InformationSystem.CustomRiskness))
                    {
                        foreach (var xSystem in item.Systems.Where(x => !x.InformationSystem.CustomRiskness))
                        {
                            var system = globalSystems.Single(x => x.Id == xSystem.InformationSystem.GdprInfoSystem.Id);
                            await SystemEvalWithItemsNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, currentItemImpact, gapRow, itemRow, system, -1);
                            if (system.Storages.Any(x => !x.GdprStorage.CustomRiskness))
                            {
                                foreach (var xStorage in system.Storages.Where(x => !x.GdprStorage.CustomRiskness))
                                {
                                    var storage = globalStorages.Single(x => x.Id == xStorage.GdprStorageId);
                                    await StorageEvalWithItemsNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, globalNodes, currentItemImpact, gapRow, itemRow, storage, -1);
                                }
                            }
                        }
                    }
                    #endregion
                }
                else
                {
                    gapRow.isRelevant = false;
                }
                if (!gapRow.isRelevant)
                {
                    gapRow.highestRisk = 0;
                }

                if (!gapRowExists)
                {
                    vm.rows.Add(gapRow);
                }
            }
            foreach (var node in globalNodes)
            {
                await LocationCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, node, riskTemplates, impactMinMax, riskLevelMinMax);
            }
            foreach (var storage in globalStorages)
            {
                await StorageCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, null, null, null, storage, riskTemplates, impactMinMax, riskLevelMinMax);
            }
            foreach (var system in globalSystems)
            {
                await SystemCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, null, null, system, riskTemplates, impactMinMax, riskLevelMinMax);
            }
            if (vm.withoutErrors)
            {
                if (vm.rows.Any())
                {
                    vm.GapRows = CreateTablesRequired(measuresForRisks, impactCategories);
                }
                // Výpis kategorií i když nejsou žádné záznamy
                vm.Categories = await CreateCategories(riskAnalysis.Id, userCompany);
            }

            return vm;
        }

        public async Task<GapReportViewModelNew> CreateReportRecordData(RiskAnalysis riskAnalysis, List<int> states, List<GapRiskItem> riskTemplates)
        {
            var userCompany = await userService.GetCurrentUserCompanyAsync();
            var vm = new GapReportViewModelNew
            {
                active = states.Any(x => x == (int)GdprRecordState.Entry.Active),
                draft = states.Any(x => x == (int)GdprRecordState.Entry.Draft),
                waiting = states.Any(x => x == (int)GdprRecordState.Entry.Waiting),
                ReportBase =
                {
                    ReportName = ReportNames.GapAnalysis,
                    CompanyName = userCompany.CompanyName,
                },
                withoutErrors = true, // Explicitní nastavení
                colors = new Dictionary<int, string>(),
                riskNames = new Dictionary<int, string>(),
            };

            vm.measureNames.AddRange(await unitOfWork.GetRepository<GdprSecurityMeasuresCategory>()
                .Query()
                .Select(x => x.Name)
                .ToListAsync());

            // Kategorie dopadů
            var impactCategories = await unitOfWork.GetRepository<Category>()
                .Query(x => x.CategoryTypeId == CategoryType.Entry.RecordImpact)
                .FilterAccessible(userCompany)
                .OrderBy(x => x.Value)
                .ToListAsync();

            // Kategorie Rizikovost
            var riskCategories = await unitOfWork.GetRepository<Category>()
                .Query(x => x.CategoryTypeId == CategoryType.Entry.RiskLevel)
                .FilterAccessible(userCompany)
                .OrderBy(x => x.Value)
                .ToListAsync();

            // potřebujeme pro převod škál při vytváření rizika. Z dopadu musíme udělat rizikovost
            // všechny čtyři hodnoty se vyskytují ve vzorci pro převod
            var impactMinMax = GetMinMaxValueFromCategories(impactCategories);
            var riskLevelMinMax = GetMinMaxValueFromCategories(riskCategories);

            foreach (var impactCategoryGroup in impactCategories.GroupBy(x => x.Value))
            {
                if (impactCategoryGroup.Count() > 1)
                {
                    vm.errorMsg = "Něco je špatně s Vašimi číselníky Hodnocení dopadů, Dostupnost, Důvěrnost, Integrita. Report není v pořádku.";
                    vm.withoutErrors = false;
                    return vm;
                }

                vm.colors.Add(impactCategoryGroup.First().Value ?? 0, "background-color: " + impactCategoryGroup.First().Color);
                vm.riskNames.Add(impactCategoryGroup.First().Value ?? 0, impactCategoryGroup.First().Name);
            }

            vm.riskNamesArray = vm.riskNames.Select(x => x.Value).ToArray();

            // GAP ANALÝZA
            // Získávání dat do Reportu z MATICE RIZIK
            // Jednotlivé buňky GAP tabulky
            // Seřazeno dle Sledovaných kategorií
            var measuresForRisks = await unitOfWork.GetRepository<GdprGAPCell>()
                .Query(x => x.RiskAnalysisId == riskAnalysis.Id)
                .FilterAccessible(userCompany)
                .Include(x => x.GdprSecurityMeasuresCategory)
                .Include(x => x.SelectedCategoryWithDeleted)
                    .ThenInclude(x => x.Category)
                .Include(x => x.RiskCategory)
                .OrderBy(x => x.GdprSecurityMeasuresCategoryId)
                .ToListAsync();

            /* všechny recordy/procesy */
            var globalRecords = await unitOfWork.GetRepository<GdprRecord>()
                .Query(x => states.Any(xx => xx == (int)x.RecordStateId))
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .OrderBy(x => x.AreaCategory.Order)
                    .ThenBy(x => x.Name)
                .ToListAsync();

            var globalSystems = await unitOfWork.GetRepository<GdprInfoSystem>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var globalStorages = await unitOfWork.GetRepository<GdprStorage>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var globalNodes = await unitOfWork.GetRepository<GdprFileServiceNode>()
                .Query()
                .FilterAccessible(userCompany)
                .IncludeDataForGapReport()
                .ToArrayAsync();

            var globalSlas = await unitOfWork.GetRepository<SLA>()
                .Query()
                .FilterAccessible(userCompany)
                .Include(x => x.Effectuality)
                .ToArrayAsync();

            var contractCats = await unitOfWork.GetRepository<GdprGAPCategory>()
                .Query(x => x.RiskAnalysisId == riskAnalysis.Id && x.CategoryTypeId == CategoryType.Entry.ContractType)
                .ToListAsync();

            var contracts = await unitOfWork.GetRepository<Contract>()
                .Query(contract => contract.CategoriesWithDeleted.Any(category =>
                        category.Lifecycle_DeletedDate == null &&
                        category.CategoryTypeId == CategoryType.Entry.ContractType &&
                        contractCats.Select(x => x.Id).Any(y => category.CategoryId == y)))
                .Include(x => x.CategoriesWithDeleted)
                .Include(x => x.CompaniesWithDeleted)
                .ToListAsync();

            var measureCount = Enum.GetValues(typeof(GdprSecurityMeasuresCategory.Entry)).Length;

            // Slovník s rizikovostí jednotlivých GDPR záznamů
            var recordImpactDict = new Dictionary<Guid, Category>();
            // Procházení jednotlivých recordů
            foreach (var gdprRecord in globalRecords)
            {
                var iEval = new RiskAnalysisEvaluatedEntity
                {
                    Availability = gdprRecord.AvailabilityLevelCategory,
                    Confidentiality = gdprRecord.ConfidentialityLevelCategory,
                    Integrity = gdprRecord.IntegrityLevelCategory,
                };

                var currentItemImpact = ReturnImpact(
                    new List<Category>
                    {
                        iEval.Availability,
                        iEval.Confidentiality,
                        iEval.Integrity,
                    }, impactCategories);

                if (!recordImpactDict.ContainsKey(gdprRecord.Id))
                {
                    recordImpactDict.Add(gdprRecord.Id, currentItemImpact);
                }

                var gapRowExists = vm.rows.Any(x => x.recordName == gdprRecord.Name);
                var gapRow = vm.rows.SingleOrDefault(x => x.recordName == gdprRecord.Name) ?? new GapReportRowViewModel
                {
                    recordId = gdprRecord.Id,
                    recordName = gdprRecord.Name,
                    recordAreaId = gdprRecord.AreaCategoryId,
                    recordAreaName = gdprRecord.AreaCategory.Name,
                    recordAreaOrder = gdprRecord.AreaCategory.Order,
                    highestRisk = currentItemImpact != null ? currentItemImpact.Value.Value : -1,
                    missingMeasures = new int[measureCount],
                };

                if (IsRelevant(iEval))
                {
                    gapRow.isRelevant = true;

                    var itemRow = new GapReportRecordViewModel
                    {
                        Id = gdprRecord.Id.ToString().ToLowerInvariant(),
                        Area = gdprRecord.AreaCategory.Name,
                        Name = gdprRecord.Name,
                        HighestRisk = currentItemImpact.Value.Value,
                    };

                    #region OrganizationalMeasures

                    var requiredItemMeasures = measuresForRisks.SingleOrDefault(
                                         x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.RecordMeasures
                                         && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);
                    var currentItemMeasures = gdprRecord.OrganizationalMeasures.Select(x => x.Category);

                    var missingItemMeasures = requiredItemMeasures != null ? requiredItemMeasures.Except(currentItemMeasures) : new List<Category>();

                    if (missingItemMeasures.Any())
                    {
                        gapRow.missingMeasures[(int)GdprSecurityMeasuresCategory.Entry.RecordMeasures - 1]++;
                        gapRow.missing = true;

                        if (riskTemplates.All(x => x.ObjectName != gdprRecord.Name && x.Type == GdprSecurityMeasuresCategory.Entry.RecordMeasures))
                        {
                            var riskItem = CreateRiskTemplateItem(gdprRecord.Id, gdprRecord.Name, GdprSecurityMeasuresCategory.Entry.RecordMeasures, currentItemImpact, riskAnalysis.Id, impactMinMax, riskLevelMinMax);
                            riskTemplates.Add(riskItem);
                        }

                        itemRow.CurrentMeasureTypes = currentItemMeasures.Select(x => x.DisplayName).ToList();
                        itemRow.MissingMeasureTypes = missingItemMeasures.Select(x => x.DisplayName).ToList();

                        foreach (var cat in missingItemMeasures)
                        {
                            if (vm.MissingMeasures.All(x => x.Name != cat.Name))
                            {
                                vm.MissingMeasures.Add(new GapReportMeasureViewModel
                                {
                                    Id = cat.Id.ToString().ToLowerInvariant(),
                                    Name = cat.DisplayName,
                                    HighestRisk = currentItemImpact.Value.Value,
                                });
                            }
                        }
                        if (vm.RecordsWithMissing.All(x => x.Name != itemRow.Name))
                        {
                            vm.RecordsWithMissing.Add(itemRow);
                        }
                    }
                    #endregion

                    #region ProcessorsMeasures

                    if (contractCats.Any())
                    {
                        foreach (var processor in gdprRecord.RecordProcessors)
                        {
                            if (contracts.All(x => x.Companies.ToList().All(c => c.CompanyId != processor.CompanyId)))
                            {
                                if (vm.CompaniesWithMissing.All(x => x.Name != processor.Company.DisplayName))
                                {
                                    vm.CompaniesWithMissing.Add(
                                        new GapReportCompanyViewModel
                                        {
                                            Type = EffitModuleType.Record,
                                            Name = processor.Company.DisplayName,
                                            Id = processor.CompanyId,
                                            HighestRisk = 0, // Hodnota je aktualizována v dalším zpracování - region Missing Contracts
                                        });
                                }

                                gapRow.missingMeasures[(int)GdprSecurityMeasuresCategory.Entry.ContractMeasures - 1]++;
                                gapRow.missing = true;

                                if (riskTemplates.All(x => x.ObjectName != gdprRecord.Name && x.Type == GdprSecurityMeasuresCategory.Entry.ContractMeasures))
                                {
                                    var riskItem = CreateRiskTemplateItem(processor.CompanyId, processor.Company.Name, GdprSecurityMeasuresCategory.Entry.ContractMeasures, currentItemImpact, riskAnalysis.Id, impactMinMax, riskLevelMinMax);
                                    riskTemplates.Add(riskItem);
                                }
                            }
                        }
                    }
                    #endregion

                    #region SystemMeasures and StorageMeasures

                    if (gdprRecord.GdprInfoSystems.Any(x => !x.GdprInfoSystem.InformationSystem.CustomRiskness))
                    {
                        foreach (var xSystem in gdprRecord.GdprInfoSystems.Where(x => !x.GdprInfoSystem.InformationSystem.CustomRiskness))
                        {
                            var system = globalSystems.Single(x => x.Id == xSystem.GdprInfoSystemId);
                            await SystemEvalWithItemsNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, currentItemImpact, gapRow, itemRow, system);

                            if (system.Storages.Any(x => !x.GdprStorage.CustomRiskness))
                            {
                                foreach (var xStorage in system.Storages.Where(x => !x.GdprStorage.CustomRiskness))
                                {
                                    var storage = globalStorages.Single(x => x.Id == xStorage.GdprStorageId);
                                    await StorageEvalWithItemsNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, globalNodes, currentItemImpact, gapRow, itemRow, storage);
                                }
                            }
                        }
                    }
                    #endregion

                    #region LocationMeasures

                    if (gdprRecord.FileServiceNodes.Any(x => !x.GdprFileServiceNode.CustomRiskness))
                    {
                        foreach (var xNode in gdprRecord.FileServiceNodes.Where(x => !x.GdprFileServiceNode.CustomRiskness))
                        {
                            var node = globalNodes.Single(x => x.Id == xNode.GdprFileServiceNodeId);
                            await LocationEvalWithItemsNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, currentItemImpact, gapRow, itemRow, node, xNode.Target);
                        }
                    }
                    #endregion
                }
                else
                {
                    gapRow.isRelevant = false;
                }

                if (!gapRow.isRelevant)
                {
                    gapRow.highestRisk = 0;
                }

                if (!gapRowExists)
                {
                    vm.rows.Add(gapRow);
                }
            }

            #region Missing Contracts
            // Doplnění GDPR záznamů pro společnosti, které mají chybějící smluvní zajištění
            if (vm.CompaniesWithMissing.Any(x => x.Type == EffitModuleType.Record))
            {
                foreach (var company in vm.CompaniesWithMissing.Where(x => x.Type == EffitModuleType.Record))
                {
                    var highestRisk = 0;
                    var processorRecords = globalRecords.Where(x => x.RecordProcessors.Any(rp => rp.CompanyId == company.Id)).ToList();
                    processorRecords.ForEach(x =>
                    {
                        company.Records.Add(new GapReportRecordViewModel
                        {
                            Name = x.DisplayName,
                            Area = x.AreaCategory.Name,
                            Id = x.Id.ToString(),
                            HighestRisk = recordImpactDict[x.Id].Value.Value,
                        });
                        if (recordImpactDict[x.Id].Value.Value > highestRisk)
                        {
                            highestRisk = recordImpactDict[x.Id].Value.Value;
                        }
                    });

                    company.HighestRisk = highestRisk;
                }
            }

            #endregion

            foreach (var node in globalNodes)
            {
                await LocationCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, node, riskTemplates, impactMinMax, riskLevelMinMax);
            }

            foreach (var storage in globalStorages)
            {
                await StorageCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, null, null, null, storage, riskTemplates, impactMinMax, riskLevelMinMax);
            }

            foreach (var system in globalSystems)
            {
                await SystemCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, null, null, system, riskTemplates, impactMinMax, riskLevelMinMax);
            }

            if (vm.withoutErrors)
            {
                if (vm.rows.Any())
                {
                    vm.GapRows = CreateTablesRequired(measuresForRisks, impactCategories);
                }
                // Výpis kategorií i když nejsou žádné záznamy
                vm.Categories = await CreateCategories(riskAnalysis.Id, userCompany);
            }
            vm.Nodes = vm.Nodes.Where(x => x.Missing && !x.OnStorage).ToList();
            vm.Storages = vm.Storages.Where(x => x.Missing).ToList();
            vm.Systems = vm.Systems.Where(x => x.Missing).ToList();

            return vm;
        }

        private async Task LocationCustomEvalNew(
            UserCompany userCompany,
            RiskAnalysis riskAnalysis,
            GapReportViewModelNew vm,
            List<Category> impactCategories,
            List<GdprGAPCell> measuresForRisks,
            GdprFileServiceNode location,
            List<GapRiskItem> riskItems,
            Tuple<int, int> currentMinMax,
            Tuple<int, int> newMinMax)
        {
            var nEval = await riskAnalysisService.GetCalculatedRiskness(location, riskAnalysis, userCompany);
            var nodeRowExists = vm.objectRows.Any(x => x.type == EffitModuleType.Location && x.name == location.Name);
            var nodeRow = new GapReportObjectRowViewModel
            {
                id = location.Id,
                type = EffitModuleType.Location,
                order = (int)EffitModuleType.Location,
                name = location.Name,
                missingMeasures = new[] { "N/A", "N/A", "N/A", "OK", "OK" },
            };

            if (!nodeRowExists)
            {
                vm.objectRows.Add(nodeRow);
            }

            if (IsRelevant(nEval))
            {
                // Lazy loading potřebných dat pro InformationSystem entity
                if (!unitOfWork.Entry(location).Collection(x => x.CategoriesWithDeleted).IsLoaded)
                {
                    unitOfWork.Entry(location).Collection(x => x.CategoriesWithDeleted)
                        .Query()
                        .Include(x => x.Category)
                        .Load();
                }

                var currentNodeImpact = ReturnImpact(new List<Category> { nEval.Availability, nEval.Confidentiality, nEval.Integrity }, impactCategories);
                nodeRow.highestRisk = currentNodeImpact.Value.Value;
                nodeRow.isRelevant = true;
                nodeRow.risknessString = location.CustomRiskness ? Constants.GapCustomRisknessString : string.Empty;

                if (riskAnalysis.IsDefault)
                {
                    var requiredNodeMeasures = measuresForRisks.SingleOrDefault(
                          x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.NodeMeasures
                          && x.RiskCategoryId == currentNodeImpact.Id)?.SelectedCategory.Select(x => x.Category);
                    var requiredNodeMeasuresT = measuresForRisks.SingleOrDefault(
                                x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures
                                && x.RiskCategoryId == currentNodeImpact.Id)?.SelectedCategory.Select(x => x.Category);

                    var currentNodeMeasures = location.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.PhysicalSecurity).Select(x => x.Category);

                    var missingNodeMeasures = requiredNodeMeasures != null ? requiredNodeMeasures.Except(currentNodeMeasures) : new List<Category>();
                    var missingNodeMeasuresT = requiredNodeMeasuresT != null ? requiredNodeMeasuresT.Except(currentNodeMeasures) : new List<Category>();

                    var noExists = vm.Nodes.Any(x => x.Name == location.Name);
                    var no = vm.Nodes.SingleOrDefault(x => x.Name == location.Name) ?? new GapReportNodeViewModel
                    {
                        Name = location.Name,
                        HighestRisk = currentNodeImpact.Value.Value,
                        Id = location.Id.ToString(),
                    };

                    if (!noExists)
                    {
                        vm.Nodes.Add(no);
                    }

                    if (!location.CustomRiskness && riskAnalysis.IsDefault)
                    {
                        foreach (var i in vm.Nodes.Single(x => x.Name == location.Name).Records)
                        {
                            if (!nodeRow.children.Any(x => x.name == i.Name && x.type == EffitModuleType.Record))
                            {
                                nodeRow.children.Add(new GapReportObjectRowViewModel
                                {
                                    id = Guid.Parse(i.Id),
                                    order = (int)EffitModuleType.Record,
                                    type = EffitModuleType.Record,
                                    name = i.Name,
                                    highestRisk = i.HighestRisk,
                                });
                            }
                        }
                    }

                    if (requiredNodeMeasures != null && requiredNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                    {
                        no.Missing = true;
                        nodeRow.missing = true;
                        nodeRow.missingMeasures[3] = "Ne";
                        no.CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value;
                        no.CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                        no.MissingSumValue = requiredNodeMeasures.Sum(x => x.Value) ?? 0 - currentNodeMeasures.Sum(x => x.Value) ?? 0;
                        no.MissingMeasureTypes = missingNodeMeasures.Select(x => x.NameWithValue).ToList();

                        if (!riskItems.Any(x => x.ObjectName == nodeRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.NodeMeasures))
                        {
                            var riskItem = CreateRiskTemplateItem(location.Id, location.Name, GdprSecurityMeasuresCategory.Entry.NodeMeasures, currentNodeImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                            var content = riskItem.ContentByRa[riskAnalysis.Id];
                            content.CurrentMeasures = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                            content.MissingMeasures = missingNodeMeasures.Select(x => x.NameWithValue).ToList();
                            content.RequiredMeasures = requiredNodeMeasures.Select(x => x.NameWithValue).ToList();
                            content.CurrentValue = currentNodeMeasures.Sum(x => x.Value).Value;
                            content.RequiredValue = requiredNodeMeasures.Sum(x => x.Value).Value;
                            content.MissingValue = requiredNodeMeasures.Sum(x => x.Value).Value - currentNodeMeasures.Sum(x => x.Value).Value;
                            riskItems.Add(riskItem);
                        }
                        else
                        {
                            var riskItem = riskItems.Single(x => x.ObjectName == nodeRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.NodeMeasures);
                            if (!riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                            {
                                var content = new GapRiskItemContent { RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentNodeImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient) };
                                content.CurrentMeasures = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                                content.MissingMeasures = missingNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                                content.RequiredMeasures = requiredNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                                content.CurrentValue = currentNodeMeasures.Sum(x => x.Value).Value;
                                content.RequiredValue = requiredNodeMeasuresT.Sum(x => x.Value).Value;
                                content.MissingValue = requiredNodeMeasuresT.Sum(x => x.Value).Value - currentNodeMeasures.Sum(x => x.Value).Value;
                                riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                            }
                        }

                        var missingValue = (requiredNodeMeasures.Sum(x => x.Value) - currentNodeMeasures.Sum(x => x.Value));
                        if (no.MissingSumValue < missingValue)
                        {
                            no.HighestRisk = currentNodeImpact.Value.Value;
                            no.MissingSumValue = missingValue.Value;
                            no.CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value;
                            no.CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                        }
                    }

                    if (requiredNodeMeasuresT != null && requiredNodeMeasuresT.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                    {
                        no.Missing = true;
                        nodeRow.missing = true;
                        nodeRow.missingMeasures[4] = "Ne";

                        if (!riskItems.Any(x => x.ObjectName == nodeRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures))
                        {
                            var riskItem = CreateRiskTemplateItem(location.Id, location.Name, GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures, currentNodeImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                            var content = riskItem.ContentByRa[riskAnalysis.Id];
                            content.CurrentMeasures = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                            content.MissingMeasures = missingNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                            content.RequiredMeasures = requiredNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                            content.CurrentValue = currentNodeMeasures.Sum(x => x.Value).Value;
                            content.RequiredValue = requiredNodeMeasuresT.Sum(x => x.Value).Value;
                            content.MissingValue = requiredNodeMeasuresT.Sum(x => x.Value).Value - currentNodeMeasures.Sum(x => x.Value).Value;
                            riskItems.Add(riskItem);
                        }
                        else
                        {
                            var riskItem = riskItems.Single(x => x.ObjectName == nodeRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures);
                            if (!riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                            {
                                var content = new GapRiskItemContent { RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentNodeImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient) };
                                content.CurrentMeasures = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                                content.MissingMeasures = missingNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                                content.RequiredMeasures = requiredNodeMeasuresT.Select(x => x.NameWithValue).ToList();
                                content.CurrentValue = currentNodeMeasures.Sum(x => x.Value).Value;
                                content.RequiredValue = requiredNodeMeasuresT.Sum(x => x.Value).Value;
                                content.MissingValue = requiredNodeMeasuresT.Sum(x => x.Value).Value - currentNodeMeasures.Sum(x => x.Value).Value;
                                riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                            }
                        }

                        var missingValue = (requiredNodeMeasuresT.Sum(x => x.Value) - currentNodeMeasures.Sum(x => x.Value));

                        if (no.MissingSumValue < missingValue)
                        {
                            no.HighestRisk = currentNodeImpact.Value.Value;
                            no.MissingSumValue = missingValue.Value;
                            no.CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value;
                            no.CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                        }
                    }
                }
                foreach (var storage in location.Storages.Select(x => x.GdprStorage))
                {
                    await StorageCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, location, nodeRow, currentNodeImpact, storage, riskItems, currentMinMax, newMinMax);
                }
            }
            else
            {
                nodeRow.highestRisk = 0;
                nodeRow.isRelevant = false;
                nodeRow.missingMeasures[3] = "N/A";
            }
        }

        private async Task StorageCustomEvalNew(
            UserCompany userCompany,
            RiskAnalysis riskAnalysis,
            GapReportViewModelNew vm,
            List<Category> impactCategories,
            List<GdprGAPCell> measuresForRisks,
            GdprFileServiceNode node,
            GapReportObjectRowViewModel nodeRow,
            Category currentNodeImpact,
            GdprStorage storage,
            List<GapRiskItem> riskItems,
            Tuple<int, int> currentMinMax,
            Tuple<int, int> newMinMax)
        {
            var tEval = await riskAnalysisService.GetCalculatedRiskness(storage, riskAnalysis, userCompany);

            var currentStorageImpact = ReturnImpact(new List<Category> { tEval.Availability, tEval.Confidentiality, tEval.Integrity }, impactCategories);
            var storageRowExists = vm.objectRows.Any(x => x.type == EffitModuleType.Storage && x.name == storage.Name);
            var storageRow = vm.objectRows.SingleOrDefault(x => x.type == EffitModuleType.Storage && x.name == storage.Name) ?? new GapReportObjectRowViewModel
            {
                id = storage.Id,
                type = EffitModuleType.Storage,
                order = (int)EffitModuleType.Storage,
                name = storage.Name,
                isOutsoursing = storage.IsOutsourced,
                isExternalManagement = storage.ExternalManagement,
                missingMeasures = new[] { "N/A", "OK", "OK", "N/A", "OK" },
                isRelevant = IsRelevant(tEval),
                highestRisk = 0,
            };

            if (!storageRowExists)
            {
                vm.objectRows.Add(storageRow);
            }

            // Lazy loading potřebných dat pro InformationSystem entity
            if (!unitOfWork.Entry(storage).Collection(x => x.SLAsWithDeleted).IsLoaded)
            {
                unitOfWork.Entry(storage).Collection(x => x.SLAsWithDeleted)
                    .Query()
                    .Include(x => x.SLA)
                    .ThenInclude(x => x.Contract)
                    .ThenInclude(x => x.CompaniesWithDeleted)
                    .Include(x => x.SLA)
                    .ThenInclude(x => x.Effectuality)
                    .Load();
            }

            if (storage.IsOutsourced)
            {
                // Lazy loading potřebných dat pro InformationSystem entity
                if (!unitOfWork.Entry(storage).Collection(x => x.StorageCompaniesWithDeleted).IsLoaded)
                {
                    unitOfWork.Entry(storage).Collection(x => x.StorageCompaniesWithDeleted)
                        .Query()
                        .Include(x => x.Company)
                        .Load();
                }

                foreach (var comp in storage.StorageCompanies)
                {
                    var companyModel = vm.CompaniesWithMissing.SingleOrDefault(x => x.Id == comp.CompanyId && x.Type == EffitModuleType.Storage);
                    if (companyModel == null)
                    {
                        companyModel = new GapReportCompanyViewModel
                        {
                            Id = comp.CompanyId,
                            Type = EffitModuleType.Storage,
                            HighestRisk = 0,
                            ExtManagement = new List<Guid>(),
                            Outsourcing = new List<Guid>() { storageRow.id },
                            Name = comp.Company.DisplayName,
                        };
                        vm.CompaniesWithMissing.Add(companyModel);
                    }
                    else
                    {
                        companyModel.Outsourcing.Add(storageRow.id);
                    }
                    var sla = storage.SLAs.Where(x => x.SLA.Contract.Companies.Any(y => y.CompanyId == comp.CompanyId));
                    if (!sla.Any(x => x.SLA.Effectuality.IsIndefinite || x.SLA.Effectuality.EffectiveTo > DateTime.Now))
                    {
                        storageRow.slaMissing = true;
                        companyModel.SlaMissing = true;
                        // Pouze pokud je úložiště hodnocené
                        if (storageRow.isRelevant)
                        {
                            if (!riskItems.Any(x => x.ObjectName == storage.Name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageSla))
                            {
                                var riskItem = CreateRiskTemplateItem(storage.Id, storage.Name, GdprSecurityMeasuresCategory.Entry.StorageSla, currentStorageImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                                riskItems.Add(riskItem);
                                var content = riskItem.ContentByRa[riskAnalysis.Id];

                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                            else
                            {
                                var riskItem = riskItems.Single(x => x.ObjectName == storage.Name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageSla);
                                GapRiskItemContent content;
                                if (riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                                {
                                    content = riskItem.ContentByRa[riskAnalysis.Id];
                                }
                                else
                                {
                                    content = new GapRiskItemContent
                                    {
                                        RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentStorageImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient),
                                        Description = riskItem.GapRiskTemplate.Description,
                                    };
                                    riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                                }
                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                        }
                    }
                }
            }

            if (storage.ExternalManagement)
            {
                // Lazy loading potřebných dat pro InformationSystem entity
                if (!unitOfWork.Entry(storage).Collection(x => x.StorageCompanyProcessorsWithDeleted).IsLoaded)
                {
                    unitOfWork.Entry(storage).Collection(x => x.StorageCompanyProcessorsWithDeleted)
                        .Query()
                        .Include(x => x.Company)
                        .Load();
                }

                foreach (var comp in storage.StorageCompanyProcessors)
                {
                    var companyModel = vm.CompaniesWithMissing.SingleOrDefault(x => x.Id == comp.CompanyId && x.Type == EffitModuleType.Storage);
                    if (companyModel == null)
                    {
                        companyModel = new GapReportCompanyViewModel
                        {
                            Id = comp.CompanyId,
                            Type = EffitModuleType.Storage,
                            HighestRisk = 0,
                            ExtManagement = new List<Guid>() { storageRow.id },
                            Outsourcing = new List<Guid>(),
                            Name = comp.Company.DisplayName,
                        };
                        vm.CompaniesWithMissing.Add(companyModel);
                    }
                    else
                    {
                        companyModel.Outsourcing.Add(storageRow.id);
                    }
                    var sla = storage.SLAs.Where(x => x.SLA.Contract.Companies.Any(y => y.CompanyId == comp.CompanyId));
                    if (!sla.Any(x => x.SLA.Effectuality.IsIndefinite || x.SLA.Effectuality.EffectiveTo > DateTime.Now))
                    {
                        storageRow.slaMissing = true;
                        companyModel.SlaMissing = true;
                        if (storageRow.isRelevant)
                        {
                            if (!riskItems.Any(x => x.ObjectName == storage.Name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageSla))
                            {
                                var riskItem = CreateRiskTemplateItem(storage.Id, storage.Name, GdprSecurityMeasuresCategory.Entry.StorageSla, currentStorageImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                                riskItems.Add(riskItem);
                                var content = riskItem.ContentByRa[riskAnalysis.Id];

                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                            else
                            {
                                var riskItem = riskItems.Single(x => x.ObjectName == storage.Name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageSla);
                                GapRiskItemContent content;
                                if (riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                                {
                                    content = riskItem.ContentByRa[riskAnalysis.Id];
                                }
                                else
                                {
                                    content = new GapRiskItemContent
                                    {
                                        RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentStorageImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient),
                                        Description = riskItem.GapRiskTemplate.Description,
                                    };
                                    riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                                }
                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                        }
                    }
                }
            }

            if (!storageRow.isRelevant)
            {
                storageRow.missingMeasures[1] = "N/A";
                storageRow.missingMeasures[2] = "N/A";
            }
            else
            {
                storageRow.highestRisk = currentStorageImpact.Value.Value;
                // Vyhledání všech záznamů pro systémy, které mají aktuální úložiště mezi ousourcovanými nebo externě spravovanými
                vm.CompaniesWithMissing
                    .Where(x => x.HighestRisk < storageRow.highestRisk && x.Type == EffitModuleType.Storage
                    && (x.ExtManagement.Any(stoId => stoId == storageRow.id) || x.Outsourcing.Any(stoId => stoId == storageRow.id)))
                    .ToList()
                    .ForEach(c => c.HighestRisk = storageRow.highestRisk);

                storageRow.isRelevant = true;
                if (storage.CustomRiskness)
                {
                    storageRow.risknessString = Constants.GapCustomRisknessString;
                    if (!(nodeRow is null))
                    {
                        nodeRow.children.Add(storageRow);
                    }
                }

                var requiredStorageMeasuresT = measuresForRisks.SingleOrDefault(
                    x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures
                    && x.RiskCategoryId == currentStorageImpact.Id)?.SelectedCategory.Select(x => x.Category);

                var currentStorageMeasuresT = storage.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.GdprStorageTechnicalSecurity).Select(x => x.Category);
                var missingStorageMeasuresT = requiredStorageMeasuresT != null ? requiredStorageMeasuresT.Except(currentStorageMeasuresT) : new List<Category>();
                if (missingStorageMeasuresT.Any())
                {
                    storageRow.missingMeasures[1] = "Ne";
                    storageRow.missing = true;
                    if (vm.Storages.All(x => x.Name != storage.Name) && !storageRow.isOutsoursing)
                    {
                        vm.Storages.Add(new GapReportStorageViewModel
                        {
                            Name = storage.Name,
                            HighestRisk = currentStorageImpact.Value.Value,
                            Id = storage.Id.ToString(),
                            Missing = true,
                            IsOutsourced = storage.IsOutsourced,
                            CurrentMeasureTypes = currentStorageMeasuresT.Select(x => x.DisplayName).ToList(),
                            MissingMeasureTypes = missingStorageMeasuresT.Select(x => x.DisplayName).ToList(),
                        });
                    }

                    if (!riskItems.Any(x => x.ObjectName == storageRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures))
                    {
                        var riskItem = CreateRiskTemplateItem(storage.Id, storage.Name, GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures, currentStorageImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                        var content = riskItem.ContentByRa[riskAnalysis.Id];
                        content.CurrentMeasures = currentStorageMeasuresT.Select(x => x.DisplayName).ToList();
                        content.MissingMeasures = missingStorageMeasuresT.Select(x => x.DisplayName).ToList();
                        content.RequiredMeasures = requiredStorageMeasuresT.Select(x => x.DisplayName).ToList();
                        riskItems.Add(riskItem);
                    }
                    else
                    {
                        var riskItem = riskItems.Single(x => x.ObjectName == storageRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures);
                        if (!riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                        {
                            var content = new GapRiskItemContent { RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentStorageImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient) };
                            content.CurrentMeasures = currentStorageMeasuresT.Select(x => x.DisplayName).ToList();
                            content.MissingMeasures = missingStorageMeasuresT.Select(x => x.DisplayName).ToList();
                            content.RequiredMeasures = requiredStorageMeasuresT.Select(x => x.DisplayName).ToList();
                            riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                        }
                    }
                }
                if (!(node is null))
                {
                    var requiredStorageMeasuresP = measuresForRisks.SingleOrDefault(
                      x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures
                      && x.RiskCategoryId == currentStorageImpact.Id)?.SelectedCategory.Select(x => x.Category);
                    var currentStorageMeasuresP = node.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.PhysicalSecurity).Select(x => x.Category);
                    var missingStorageMeasuresP = requiredStorageMeasuresP != null ? requiredStorageMeasuresP.Except(currentStorageMeasuresP) : new List<Category>();

                    if (requiredStorageMeasuresP != null && requiredStorageMeasuresP.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentStorageMeasuresP.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                    {
                        storageRow.missingMeasures[2] = "Ne";

                        storageRow.missingSecondaryType = true;
                        if (vm.Storages.All(x =>
                            x.Name != storage.Name) && !storageRow.isOutsoursing)
                        {
                            vm.Storages.Add(new GapReportStorageViewModel
                            {
                                Name = storage.Name,
                                Missing = true,
                                IsOutsourced = storage.IsOutsourced,
                                HighestRisk = currentStorageImpact.Value.Value,
                                Id = storage.Id.ToString(),
                            });
                        }

                        if (!riskItems.Any(x => x.ObjectName == storageRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures))
                        {
                            var riskItem = CreateRiskTemplateItem(storage.Id, storage.Name, GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures, currentStorageImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                            var content = riskItem.ContentByRa[riskAnalysis.Id];
                            content.CurrentMeasures = currentStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                            content.MissingMeasures = missingStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                            content.RequiredMeasures = requiredStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                            content.CurrentValue = currentStorageMeasuresP.Sum(x => x.Value).Value;
                            content.RequiredValue = requiredStorageMeasuresP.Sum(x => x.Value).Value;
                            content.MissingValue = requiredStorageMeasuresP.Sum(x => x.Value).Value - currentStorageMeasuresP.Sum(x => x.Value).Value;
                            riskItems.Add(riskItem);
                        }
                        else
                        {
                            var riskItem = riskItems.Single(x => x.ObjectName == storageRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures);
                            if (!riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                            {
                                var content = new GapRiskItemContent { RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentStorageImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient) };
                                content.CurrentMeasures = currentStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                                content.MissingMeasures = missingStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                                content.RequiredMeasures = requiredStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                                content.CurrentValue = currentStorageMeasuresP.Sum(x => x.Value).Value;
                                content.RequiredValue = requiredStorageMeasuresP.Sum(x => x.Value).Value;
                                content.MissingValue = requiredStorageMeasuresP.Sum(x => x.Value).Value - currentStorageMeasuresP.Sum(x => x.Value).Value;
                                riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                            }
                        }

                        var sto = vm.Storages.Single(x => x.Name == storage.Name);
                        var no = sto.Nodes.SingleOrDefault(x => x.Name == node.Name) ?? new GapReportNodeViewModel
                        {
                            Name = node.Name,
                            Id = node.Id.ToString(),
                            Missing = true,
                            CurrentSumValue = currentStorageMeasuresP.Sum(x => x.Value).Value,
                            MissingSumValue = requiredStorageMeasuresP.Sum(x => x.Value).Value - currentStorageMeasuresP.Sum(x => x.Value).Value,
                            CurrentMeasureTypes =
                                    currentStorageMeasuresP.Select(x => x.NameWithValue).ToList(),
                            HighestRisk = currentNodeImpact.Value.Value,
                        };
                        if (no.HighestRisk < currentNodeImpact.Value)
                        {
                            no.CurrentSumValue = currentStorageMeasuresP.Sum(x => x.Value).Value;
                            no.MissingSumValue = requiredStorageMeasuresP.Sum(x => x.Value).Value - currentStorageMeasuresP.Sum(x => x.Value).Value;
                            no.CurrentMeasureTypes = currentStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                            no.HighestRisk = currentNodeImpact.Value.Value;
                        }
                        if (sto.Nodes.All(x => x.Name != no.Name))
                        {
                            sto.Nodes.Add(no);
                        }
                    }
                }
                foreach (var system in storage.GdprInfoSystems.Select(x => x.GdprInfoSystem))
                {
                    await SystemCustomEvalNew(userCompany, riskAnalysis, vm, impactCategories, measuresForRisks, nodeRow, storageRow, system, riskItems, currentMinMax, newMinMax);
                }
            }
        }

        private async Task SystemCustomEvalNew(
            UserCompany userCompany,
            RiskAnalysis riskAnalysis,
            GapReportViewModelNew vm,
            List<Category> impactCategories,
            List<GdprGAPCell> measuresForRisks,
            GapReportObjectRowViewModel nodeRow,
            GapReportObjectRowViewModel storageRow,
            GdprInfoSystem system,
            List<GapRiskItem> riskItems,
            Tuple<int, int> currentMinMax,
            Tuple<int, int> newMinMax)
        {
            var sEval = await riskAnalysisService.GetCalculatedRiskness(system.InformationSystem, riskAnalysis, userCompany);
            var currentSystemImpact = ReturnImpact(new List<Category> { sEval.Availability, sEval.Confidentiality, sEval.Integrity }, impactCategories);

            var systemRowExists = vm.objectRows.Any(x => x.type == EffitModuleType.System && x.name == system.InformationSystem.Name);
            var systemRow = vm.objectRows.SingleOrDefault(x => x.type == EffitModuleType.System && x.name == system.InformationSystem.Name) ?? new GapReportObjectRowViewModel
            {
                id = system.InformationSystemId,
                type = EffitModuleType.System,
                order = (int)EffitModuleType.System,
                name = system.InformationSystem.Name,
                isOutsoursing = system.InformationSystem.Outsourcing,
                isExternalManagement = system.InformationSystem.ExternalManagement,
                missingMeasures = new[] { "OK", "OK", "OK", "N/A", "OK" },
                slaMissing = false,
                isRelevant = IsRelevant(sEval),
                highestRisk = 0,
            };

            if (!systemRowExists)
            {
                vm.objectRows.Add(systemRow);
            }

            // Lazy loading potřebných dat pro InformationSystem entity
            if (!unitOfWork.Entry(system.InformationSystem).Collection(x => x.SLAsWithDeleted).IsLoaded)
            {
                unitOfWork.Entry(system.InformationSystem).Collection(x => x.SLAsWithDeleted)
                    .Query()
                    .Include(x => x.SLA)
                    .ThenInclude(x => x.Contract)
                    .ThenInclude(x => x.CompaniesWithDeleted)
                    .Include(x => x.SLA)
                    .ThenInclude(x => x.Effectuality)
                    .Load();
            }

            if (system.InformationSystem.Outsourcing)
            {
                // Lazy loading potřebných dat pro InformationSystem entity
                if (!unitOfWork.Entry(system.InformationSystem).Collection(x => x.SystemCompaniesWithDeleted).IsLoaded)
                {
                    unitOfWork.Entry(system.InformationSystem).Collection(x => x.SystemCompaniesWithDeleted)
                        .Query()
                        .Include(x => x.Company)
                        .Load();
                }

                foreach (var comp in system.InformationSystem.SystemCompanies)
                {
                    var companyModel = vm.CompaniesWithMissing.SingleOrDefault(x => x.Id == comp.CompanyId && x.Type == EffitModuleType.System);
                    if (companyModel == null)
                    {
                        companyModel = new GapReportCompanyViewModel
                        {
                            Id = comp.CompanyId,
                            Type = EffitModuleType.System,
                            HighestRisk = 0,
                            ExtManagement = new List<Guid>(),
                            Outsourcing = new List<Guid>() { systemRow.id },
                            Name = comp.Company.DisplayName,
                        };
                        vm.CompaniesWithMissing.Add(companyModel);
                    }
                    else
                    {
                        companyModel.Outsourcing.Add(systemRow.id);
                    }
                    var sla = system.InformationSystem.SLAs.Where(x => x.SLA.Contract != null && x.SLA.Contract.Companies.Any(y => y.CompanyId == comp.CompanyId));
                    if (!sla.Any(x => x.SLA.Effectuality != null && (x.SLA.Effectuality.IsIndefinite || x.SLA.Effectuality.EffectiveTo > DateTime.Now)))
                    {
                        systemRow.slaMissing = true;
                        companyModel.SlaMissing = true;
                        // Pouze pokud má systém hodnocení
                        if (systemRow.isRelevant)
                        {
                            if (!riskItems.Any(x => x.ObjectName == system.InformationSystem.Name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemSla))
                            {
                                var riskItem = CreateRiskTemplateItem(system.InformationSystemId, system.InformationSystem.Name, GdprSecurityMeasuresCategory.Entry.SystemSla, currentSystemImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                                riskItems.Add(riskItem);
                                var content = riskItem.ContentByRa[riskAnalysis.Id];

                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                            else
                            {
                                var riskItem = riskItems.Single(x => x.ObjectName == system.InformationSystem.Name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemSla);
                                GapRiskItemContent content;
                                if (riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                                {
                                    content = riskItem.ContentByRa[riskAnalysis.Id];
                                }
                                else
                                {
                                    content = new GapRiskItemContent
                                    {
                                        RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentSystemImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient),
                                        Description = riskItem.GapRiskTemplate.Description,
                                    };
                                    riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                                }
                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                        }
                    }
                }
            }

            if (system.InformationSystem.ExternalManagement)
            {
                // Lazy loading potřebných dat pro InformationSystem entity
                if (!unitOfWork.Entry(system.InformationSystem).Collection(x => x.SystemCompanyProcessorsWithDeleted).IsLoaded)
                {
                    unitOfWork.Entry(system.InformationSystem).Collection(x => x.SystemCompanyProcessorsWithDeleted)
                        .Query()
                        .Include(x => x.Company)
                        .Load();
                }

                foreach (var comp in system.InformationSystem.SystemCompanyProcessors)
                {
                    var companyModel = vm.CompaniesWithMissing.SingleOrDefault(x => x.Id == comp.CompanyId && x.Type == EffitModuleType.System);
                    if (companyModel == null)
                    {
                        companyModel = new GapReportCompanyViewModel
                        {
                            Id = comp.CompanyId,
                            Type = EffitModuleType.System,
                            HighestRisk = 0,
                            ExtManagement = new List<Guid>() { systemRow.id },
                            Outsourcing = new List<Guid>(),
                            Name = comp.Company.DisplayName,
                        };
                        vm.CompaniesWithMissing.Add(companyModel);
                    }
                    else
                    {
                        companyModel.ExtManagement.Add(systemRow.id);
                    }
                    var sla = system.InformationSystem.SLAs.Where(x => x.SLA.Contract != null && x.SLA.Contract.Companies.Any(y => y.CompanyId == comp.CompanyId));
                    if (!sla.Any(x => x.SLA.Effectuality != null && (x.SLA.Effectuality.IsIndefinite || x.SLA.Effectuality.EffectiveTo > DateTime.Now)))
                    {
                        systemRow.slaMissing = true;
                        companyModel.SlaMissing = true;
                        if (systemRow.isRelevant)
                        {
                            if (!riskItems.Any(x => x.ObjectName == system.InformationSystem.Name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemSla))
                            {
                                var riskItem = CreateRiskTemplateItem(system.InformationSystemId, system.InformationSystem.Name, GdprSecurityMeasuresCategory.Entry.SystemSla, currentSystemImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                                riskItems.Add(riskItem);
                                var content = riskItem.ContentByRa[riskAnalysis.Id];

                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                            else
                            {
                                var riskItem = riskItems.Single(x => x.ObjectName == system.InformationSystem.Name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemSla);
                                GapRiskItemContent content;
                                if (riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                                {
                                    content = riskItem.ContentByRa[riskAnalysis.Id];
                                }
                                else
                                {
                                    content = new GapRiskItemContent
                                    {
                                        RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentSystemImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient),
                                        Description = riskItem.GapRiskTemplate.Description,
                                    };
                                    riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                                }
                                if (content.MissingMeasures.All(x => x != comp.Company.Name))
                                {
                                    content.MissingMeasures.Add(comp.Company.Name);
                                }
                            }
                        }
                    }
                }
            }

            if (!systemRow.isRelevant)
            {
                systemRow.missingMeasures[0] = "N/A";
            }
            else
            {
                if (!(storageRow is null))
                {
                    if (storageRow.missing)
                    {
                        systemRow.missingMeasures[1] = "Ne";
                        systemRow.missingSecondaryType = true;
                    }
                    if (storageRow.missingSecondaryType)
                    {
                        systemRow.missingMeasures[2] = "Ne";
                        systemRow.missingSecondaryType = true;
                    }
                }

                systemRow.highestRisk = currentSystemImpact.Value.Value;
                // Vyhledání všech záznamů pro systémy, které mají aktuální systém mezi ousourcovanými nebo externě spravovanými
                vm.CompaniesWithMissing
                    .Where(x => x.HighestRisk < systemRow.highestRisk && x.Type == EffitModuleType.System
                    && (x.ExtManagement.Any(sysId => sysId == systemRow.id) || x.Outsourcing.Any(sysId => sysId == systemRow.id)))
                    .ToList()
                    .ForEach(c => c.HighestRisk = systemRow.highestRisk);

                var requiredSystemMeasures = measuresForRisks.SingleOrDefault(
                    x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.SystemMeasures
                    && x.RiskCategoryId == currentSystemImpact.Id)?.SelectedCategory.Select(x => x.Category);

                var currentSystemMeasures = system.Categories
                                    .Where(x => x.CategoryTypeId == CategoryType.Entry.GdprSystemTechnicalSecurity)
                                    .Select(x => x.Category)
                                    .ToList();

                var missingSystemMeasures = requiredSystemMeasures != null ? requiredSystemMeasures.Except(currentSystemMeasures).ToList() : new List<Category>();

                if (missingSystemMeasures.Any())
                {
                    systemRow.missingMeasures[0] = "Ne";
                    systemRow.missing = true;
                    if (!riskItems.Any(x => x.ObjectName == systemRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemMeasures))
                    {
                        var riskItem = CreateRiskTemplateItem(system.InformationSystemId, system.InformationSystem.Name, GdprSecurityMeasuresCategory.Entry.SystemMeasures, currentSystemImpact, riskAnalysis.Id, currentMinMax, newMinMax);
                        var content = riskItem.ContentByRa[riskAnalysis.Id];
                        content.CurrentMeasures = currentSystemMeasures.Select(x => x.DisplayName).ToList();
                        content.MissingMeasures = missingSystemMeasures.Select(x => x.DisplayName).ToList();
                        content.RequiredMeasures = requiredSystemMeasures.Select(x => x.DisplayName).ToList();
                        riskItems.Add(riskItem);
                    }
                    else
                    {
                        var riskItem = riskItems.Single(x => x.ObjectName == systemRow.name && x.Type == GdprSecurityMeasuresCategory.Entry.SystemMeasures);
                        if (!riskItem.ContentByRa.ContainsKey(riskAnalysis.Id))
                        {
                            var content = new GapRiskItemContent { RiskLevel = (int)(GetRelativeGapRiskLevelValue(currentSystemImpact.Value.Value, currentMinMax, newMinMax) * riskItem.GapRiskTemplate.Coefficient) };
                            content.CurrentMeasures = currentSystemMeasures.Select(x => x.DisplayName).ToList();
                            content.MissingMeasures = missingSystemMeasures.Select(x => x.DisplayName).ToList();
                            content.RequiredMeasures = requiredSystemMeasures.Select(x => x.DisplayName).ToList();
                            riskItem.ContentByRa.Add(riskAnalysis.Id, content);
                        }
                    }
                }
                if (systemRow.missing && !systemRow.isOutsoursing)
                {
                    if (vm.Systems.All(x => x.Name != system.InformationSystem.Name))
                    {
                        vm.Systems.Add(
                            new GapReportSystemViewModel
                            {
                                Name = system.InformationSystem.Name,
                                Id = system.InformationSystem.Id.ToString(),
                                Missing = true,
                                IsOutsourced = system.InformationSystem.Outsourcing,
                                CurrentMeasureTypes = currentSystemMeasures.Select(x => x.DisplayName).ToList(),
                                MissingMeasureTypes = missingSystemMeasures.Select(x => x.DisplayName).ToList(),
                                HighestRisk = currentSystemImpact.Value.Value,
                            });
                    }
                    else
                    {
                        var systemWithItems = vm.Systems.Single(x => x.Name == system.InformationSystem.Name);
                        systemWithItems.Missing = true;
                    }
                }
                if (system.InformationSystem.CustomRiskness)
                {
                    if (!(storageRow is null))
                    {
                        if (string.IsNullOrEmpty(storageRow.risknessString))
                        {
                            if (!storageRow.children.Any(x => x.name == systemRow.name && x.type == EffitModuleType.System))
                            {
                                storageRow.children.Add(systemRow);
                            }

                            if (!(nodeRow is null))
                            {
                                if (!nodeRow.children.Any(x => x.name == systemRow.name && x.type == EffitModuleType.System))
                                {
                                    nodeRow.children.Add(systemRow);
                                }
                            }
                        }
                    }
                    systemRow.risknessString = Constants.GapCustomRisknessString;
                }
                else
                {
                    systemRow.children = vm.Systems.SingleOrDefault(x => x.Name == system.InformationSystem.Name)?.Records.Select(x => new GapReportObjectRowViewModel
                    {
                        id = Guid.Parse(x.Id),
                        type = EffitModuleType.Record,
                        order = (int)EffitModuleType.Record,
                        name = x.Name,
                        highestRisk = x.HighestRisk,
                    }).ToList();

                    if (!(storageRow is null))
                    {
                        if (string.IsNullOrEmpty(storageRow.risknessString))
                        {
                            if (vm.Systems.SingleOrDefault(x => x.Name == system.InformationSystem.Name) != null)
                            {
                                foreach (var i in vm.Systems.Single(x => x.Name == system.InformationSystem.Name).Records)
                                {
                                    if (!storageRow.children.Any(x => x.name == i.Name && x.type == EffitModuleType.Record))
                                    {
                                        storageRow.children.Add(new GapReportObjectRowViewModel
                                        {
                                            id = Guid.Parse(i.Id),
                                            type = EffitModuleType.Record,
                                            order = (int)EffitModuleType.Record,
                                            name = i.Name,
                                            highestRisk = i.HighestRisk,
                                        });
                                    }
                                    if (!(nodeRow is null))
                                    {
                                        if (!nodeRow.children.Any(x => x.name == i.Name && x.type == EffitModuleType.Record))
                                        {
                                            nodeRow.children.Add(new GapReportObjectRowViewModel
                                            {
                                                id = Guid.Parse(i.Id),
                                                type = EffitModuleType.Record,
                                                order = (int)EffitModuleType.Record,
                                                name = i.Name,
                                                highestRisk = i.HighestRisk,
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task LocationEvalWithItemsNew(
            UserCompany userCompany,
            RiskAnalysis riskAnalysis,
            GapReportViewModelNew vm,
            List<Category> impactCategories,
            List<GdprGAPCell> measuresForRisks,
            Category currentItemImpact,
            GapReportRowViewModel gapRow,
            GapReportRecordViewModel itemRow,
            GdprFileServiceNode node,
            bool target)
        {
            var nEval = await riskAnalysisService.GetCalculatedRiskness(node, riskAnalysis, userCompany);

            if (IsRelevant(nEval))
            {
                var currentNodeImpact = ReturnImpact(new List<Category> { nEval.Availability, nEval.Confidentiality, nEval.Integrity }, impactCategories);

                var requiredNodeMeasures = measuresForRisks.SingleOrDefault(
                        x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.NodeMeasures
                        && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);
                var requiredNodeMeasuresT = measuresForRisks.SingleOrDefault(
                            x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures
                            && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);

                var currentNodeMeasures = node.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.PhysicalSecurity).Select(x => x.Category);

                var missingNodeMeasures = requiredNodeMeasures != null ? requiredNodeMeasures.Except(currentNodeMeasures) : new List<Category>();
                var missingNodeMeasuresT = requiredNodeMeasuresT != null ? requiredNodeMeasuresT.Except(currentNodeMeasures) : new List<Category>();

                var nodeRowExists = vm.Nodes.Any(x => x.Name == node.Name);
                var nodeRow = vm.Nodes.SingleOrDefault(x => x.Name == node.Name) ?? new GapReportNodeViewModel
                {
                    Name = node.Name,
                    HighestRisk = currentItemImpact.Value.Value,
                    Id = node.Id.ToString(),
                    CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value,
                    CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList(),
                    MissingMeasureTypes = missingNodeMeasures.Select(x => x.NameWithValue).ToList(),
                };

                if (!nodeRowExists)
                {
                    nodeRow.MissingSumValue = requiredNodeMeasures == null ? 0 : (requiredNodeMeasures.Sum(x => x.Value) - currentNodeMeasures.Sum(x => x.Value)).Value;
                    vm.Nodes.Add(nodeRow);
                }

                if (nodeRow.Records.All(x => x.Name != itemRow.Name))
                {
                    nodeRow.Records.Add(itemRow);
                }

                if (!target && requiredNodeMeasures != null && requiredNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                {
                    gapRow.missingMeasures[(int)GdprSecurityMeasuresCategory.Entry.NodeMeasures - 1]++;
                    nodeRow.Missing = true;

                    var missingValue = (requiredNodeMeasures.Sum(x => x.Value) - currentNodeMeasures.Sum(x => x.Value));
                    if (nodeRow.MissingSumValue < missingValue)
                    {
                        nodeRow.HighestRisk = currentItemImpact.Value.Value;
                        nodeRow.MissingSumValue = missingValue.Value;
                        nodeRow.CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value;
                        nodeRow.CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                    }
                }

                if (target && requiredNodeMeasuresT != null && requiredNodeMeasuresT.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentNodeMeasures.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                {
                    gapRow.missingMeasures[(int)GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures - 1]++;
                    nodeRow.Missing = true;

                    var missingValue = (requiredNodeMeasuresT.Sum(x => x.Value) - currentNodeMeasures.Sum(x => x.Value));

                    if (nodeRow.MissingSumValue < missingValue)
                    {
                        nodeRow.HighestRisk = currentItemImpact.Value.Value;
                        nodeRow.MissingSumValue = missingValue.Value;
                        nodeRow.CurrentSumValue = currentNodeMeasures.Sum(x => x.Value).Value;
                        nodeRow.CurrentMeasureTypes = currentNodeMeasures.Select(x => x.NameWithValue).ToList();
                    }
                }
            }
        }

        private async Task StorageEvalWithItemsNew(UserCompany userCompany, RiskAnalysis riskAnalysis, GapReportViewModelNew vm, List<Category> impactCategories, List<GdprGAPCell> measuresForRisks, GdprFileServiceNode[] globalNodes, Category currentItemImpact, GapReportRowViewModel gapRow, GapReportRecordViewModel itemRow, GdprStorage storage, int shift = 0)
        {
            var requiredStorageMeasuresT = measuresForRisks.SingleOrDefault(
                x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures
                && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);
            var requiredStorageMeasuresP = measuresForRisks.SingleOrDefault(
                x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures
                && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);
            var tEval = await riskAnalysisService.GetCalculatedRiskness(storage, riskAnalysis, userCompany);

            var currentStorageMeasuresT = storage.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.GdprStorageTechnicalSecurity).Select(x => x.Category);
            var missingStorageMeasuresT = requiredStorageMeasuresT != null ? requiredStorageMeasuresT.Except(currentStorageMeasuresT) : new List<Category>();

            if (IsRelevant(tEval))
            {
                var currentStorageImpact = ReturnImpact(new List<Category> { tEval.Availability, tEval.Confidentiality, tEval.Integrity }, impactCategories);

                var storageRowExists = vm.Storages.Any(x => x.Name == storage.Name);
                var storageRow = vm.Storages.SingleOrDefault(x => x.Name == storage.Name) ?? new GapReportStorageViewModel
                {
                    Name = storage.Name,
                    HighestRisk = currentItemImpact.Value.Value,
                    Id = storage.Id.ToString(),
                    IsOutsourced = storage.IsOutsourced,
                    CurrentMeasureTypes = currentStorageMeasuresT.Select(x => x.NameWithValue).ToList(),
                    MissingMeasureTypes = missingStorageMeasuresT.Select(x => x.NameWithValue).ToList(),
                };
                if (storageRow.Records.All(x => x.Name != itemRow.Name))
                {
                    storageRow.Records.Add(itemRow);
                }
                if (!storageRowExists)
                {
                    vm.Storages.Add(storageRow);
                }
                if (missingStorageMeasuresT.Any())
                {
                    gapRow.missingMeasures[shift + (int)GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures - 1]++;
                    storageRow.Missing = true;
                    var storageToEdit = vm.Storages.Single(x => x.Name == storage.Name);
                    if (storageToEdit.HighestRisk < currentItemImpact.Value.Value)
                    {
                        storageToEdit.HighestRisk = currentItemImpact.Value.Value;
                        storageToEdit.CurrentMeasureTypes = currentStorageMeasuresT.Select(x => x.NameWithValue).ToList();
                        storageToEdit.MissingMeasureTypes = missingStorageMeasuresT.Select(x => x.NameWithValue).ToList();
                    }
                }
                if (storage.FileServiceNodes.Any(x => !x.GdprFileServiceNode.CustomRiskness))
                {
                    foreach (var xNode in storage.FileServiceNodes.Where(x => !x.GdprFileServiceNode.CustomRiskness))
                    {
                        var node = globalNodes.Single(x => x.Id == xNode.GdprFileServiceNodeId);
                        var nEval = await riskAnalysisService.GetCalculatedRiskness(node, riskAnalysis, userCompany);
                        var currentNodeImpact = ReturnImpact(new List<Category> { nEval.Availability, nEval.Confidentiality, nEval.Integrity }, impactCategories);

                        var currentStorageMeasuresP = node.Categories.Where(x => x.CategoryTypeId == CategoryType.Entry.PhysicalSecurity).Select(x => x.Category);
                        var missingStorageMeasuresP = requiredStorageMeasuresP != null ? requiredStorageMeasuresP.Except(currentStorageMeasuresP) : new List<Category>();

                        if (requiredStorageMeasuresP != null && requiredStorageMeasuresP.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value) > currentStorageMeasuresP.Where(x => x != null && x.Value.HasValue).Sum(x => x.Value.Value))
                        {
                            var nodeRow = storageRow.Nodes.SingleOrDefault(x => x.Name == node.Name) ?? new GapReportNodeViewModel
                            {
                                Name = node.Name,
                                HighestRisk = currentItemImpact.Value.Value,
                                Id = node.Id.ToString(),
                                //OnStorage = true,
                                CurrentSumValue = currentStorageMeasuresP.Sum(x => x.Value).Value,
                                CurrentMeasureTypes = currentStorageMeasuresP.Select(x => x.NameWithValue).ToList(),
                                MissingSumValue = (requiredStorageMeasuresP.Sum(x => x.Value) - currentStorageMeasuresP.Sum(x => x.Value)).Value,
                                MissingMeasureTypes = missingStorageMeasuresP.Select(x => x.NameWithValue).ToList(),
                            };

                            if (nodeRow.Records.All(x => x.Name != itemRow.Name))
                            {
                                nodeRow.Records.Add(itemRow);
                            }

                            gapRow.missingMeasures[shift + (int)GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures - 1]++;
                            storageRow.Missing = true;

                            if (storageRow.Nodes.All(x => x.Name != nodeRow.Name))
                            {
                                storageRow.Nodes.Add(nodeRow);
                            }
                            if (storageRow.HighestRisk < currentItemImpact.Value.Value)
                            {
                                nodeRow.HighestRisk = currentItemImpact.Value.Value;
                                nodeRow.CurrentSumValue = currentStorageMeasuresP.Sum(x => x.Value).Value;
                                nodeRow.CurrentMeasureTypes = currentStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                                nodeRow.MissingSumValue = (requiredStorageMeasuresP.Sum(x => x.Value) - currentStorageMeasuresP.Sum(x => x.Value)).Value;
                                nodeRow.MissingMeasureTypes = missingStorageMeasuresP.Select(x => x.NameWithValue).ToList();
                                storageRow.HighestRisk = currentItemImpact.Value.Value;
                                storageRow.CurrentMeasureTypes = currentStorageMeasuresT.Select(x => x.NameWithValue).ToList();
                                storageRow.MissingMeasureTypes = missingStorageMeasuresT.Select(x => x.NameWithValue).ToList();
                            }
                        }
                    }
                }
            }
        }

        private async Task SystemEvalWithItemsNew(
            UserCompany userCompany,
            RiskAnalysis riskAnalysis,
            GapReportViewModelNew vm,
            List<Category> impactCategories,
            List<GdprGAPCell> measuresForRisks,
            Category currentItemImpact,
            GapReportRowViewModel gapRow,
            GapReportRecordViewModel itemRow,
            GdprInfoSystem system,
            int shift = 0)
        {
            var sEval = await riskAnalysisService.GetCalculatedRiskness(system.InformationSystem, riskAnalysis, userCompany);
            if (IsRelevant(sEval))
            {
                var systemRowExists = vm.Systems.Any(x => x.Name == system.InformationSystem.Name);
                var systemRow = vm.Systems.SingleOrDefault(x => x.Name == system.InformationSystem.Name) ?? new GapReportSystemViewModel
                {
                    Name = system.InformationSystem.Name,
                    Id = system.InformationSystem.Id.ToString(),
                    IsOutsourced = system.InformationSystem.Outsourcing,
                    HighestRisk = currentItemImpact.Value.Value,
                };

                if (!systemRowExists)
                {
                    vm.Systems.Add(systemRow);
                }
                if (systemRow.Records.All(x => x.Name != itemRow.Name))
                {
                    systemRow.Records.Add(itemRow);
                }
                var currentSystemImpact = ReturnImpact(new List<Category> { sEval.Availability, sEval.Confidentiality, sEval.Integrity }, impactCategories);

                var requiredSystemMeasures = measuresForRisks.SingleOrDefault(
                    x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.SystemMeasures
                    && x.RiskCategoryId == currentItemImpact.Id)?.SelectedCategory.Select(x => x.Category);

                var currentSystemMeasures = system.Categories
                                    .Where(x => x.CategoryTypeId == CategoryType.Entry.GdprSystemTechnicalSecurity)
                                    .Select(x => x.Category)
                                    .ToList();

                var missingSystemMeasures = requiredSystemMeasures != null ? requiredSystemMeasures.Except(currentSystemMeasures).ToList() : new List<Category>();

                if (missingSystemMeasures.Any())
                {
                    if (!systemRowExists)
                    {
                        systemRow.CurrentMeasureTypes = currentSystemMeasures.Select(x => x.DisplayName).ToList();
                        systemRow.MissingMeasureTypes = missingSystemMeasures.Select(x => x.DisplayName).ToList();
                    }
                    systemRow.Missing = true;

                    gapRow.missingMeasures[shift + (int)GdprSecurityMeasuresCategory.Entry.SystemMeasures - 1]++;

                    if (systemRow.HighestRisk < currentItemImpact.Value.Value)
                    {
                        systemRow.HighestRisk = currentItemImpact.Value.Value;
                        systemRow.CurrentMeasureTypes = currentSystemMeasures.Select(x => x.NameWithValue).ToList();
                        systemRow.MissingMeasureTypes = missingSystemMeasures.Select(x => x.NameWithValue).ToList();
                    }
                }
            }
        }

        public async Task<GapReportCategoriesViewModel> CreateCategories(Guid riskAnalysisId, UserCompany uc)
        {
            var catType = unitOfWork.GetRepository<CategoryType>().Query();
            var phy = await catType.SingleAsync(x => x.Id == CategoryType.Entry.PhysicalSecurity);
            var avai = await catType.SingleAsync(x => x.Id == CategoryType.Entry.AvailabilityLevel);
            var conf = await catType.SingleAsync(x => x.Id == CategoryType.Entry.ConfidentialityLevel);
            var inte = await catType.SingleAsync(x => x.Id == CategoryType.Entry.IntegrityLevel);
            return new GapReportCategoriesViewModel
            {
                PhysicalName = phy.Name,
                PhysicalNote = phy.Description,
                Physical = await unitOfWork.GetRepository<Category>()
                        .Query(x => x.UserCompanyId == uc.Id && x.CategoryTypeId == CategoryType.Entry.PhysicalSecurity)
                        .OrderBy(x => x.Order)
                        .Select(x => new List<string> { x.Name, x.Value.ToString(), x.Note })
                        .ToListAsync(),
                AvailabilityName = avai.Name,
                AvailabilityNote = avai.Description,
                Availability = await unitOfWork.GetRepository<RiskAnalysisToCategory>()
                        .Query(x => x.RiskAnalysisId == riskAnalysisId && x.Category.UserCompanyId == uc.Id && x.CategoryTypeId == CategoryType.Entry.AvailabilityLevel)
                        .Include(x => x.Category)
                        .OrderBy(x => x.Category.Order)
                        .Select(x => new List<string> { x.Category.Name, x.Category.Value.ToString(), x.Category.Note })
                        .ToListAsync(),
                ConfidentialityName = conf.Name,
                ConfidentialityNote = conf.Description,
                Confidentiality = await unitOfWork.GetRepository<RiskAnalysisToCategory>()
                        .Query(x => x.RiskAnalysisId == riskAnalysisId && x.Category.UserCompanyId == uc.Id && x.CategoryTypeId == CategoryType.Entry.ConfidentialityLevel)
                        .Include(x => x.Category)
                        .OrderBy(x => x.Category.Order)
                        .Select(x => new List<string> { x.Category.Name, x.Category.Value.ToString(), x.Category.Note })
                        .ToListAsync(),
                IntegrityName = inte.Name,
                IntegrityNote = inte.Description,
                Integrity = await unitOfWork.GetRepository<RiskAnalysisToCategory>()
                        .Query(x => x.RiskAnalysisId == riskAnalysisId && x.Category.UserCompanyId == uc.Id && x.CategoryTypeId == CategoryType.Entry.IntegrityLevel)
                        .Include(x => x.Category)
                        .OrderBy(x => x.Category.Order)
                        .Select(x => new List<string> { x.Category.Name, x.Category.Value.ToString(), x.Category.Note })
                        .ToListAsync(),
            };
        }

        public async Task EvaluateRiskImpact(ModelStateDictionary modelState, GdprRecord dbEntity, RecordEvaluationEditViewModel vm)
        {
            var ra = await riskAnalysisService.GetDefaultAnalysis(await userService.GetCurrentUserCompanyAsync(), modelState);
            if (!string.IsNullOrWhiteSpace(vm.Availability))
            {
                var dbCatL = await riskAnalysisService.GetCategoryByTypeAndRiskAnalysis(ra.Id, vm.Availability, CategoryType.Entry.AvailabilityLevel);

                if (dbCatL == null)
                {
                    modelState.AddModelError<RecordEvaluationEditViewModel>(x => x.Availability, "Dopad nenalezen.");
                }

                dbEntity.AvailabilityLevelCategoryId = dbCatL.Id;
            }
            else
            {
                dbEntity.AvailabilityLevelCategoryId = null;
            }
            if (!string.IsNullOrWhiteSpace(vm.Confidentiality))
            {
                var dbCatP = await riskAnalysisService.GetCategoryByTypeAndRiskAnalysis(ra.Id, vm.Confidentiality, CategoryType.Entry.ConfidentialityLevel);

                if (dbCatP == null)
                {
                    modelState.AddModelError<RecordEvaluationEditViewModel>(x => x.Confidentiality, "Dopad nenalezen.");
                }

                dbEntity.ConfidentialityLevelCategoryId = dbCatP.Id;
            }
            else
            {
                dbEntity.ConfidentialityLevelCategoryId = null;
            }
            if (!string.IsNullOrWhiteSpace(vm.Integrity))
            {
                var dbCatA = await riskAnalysisService.GetCategoryByTypeAndRiskAnalysis(ra.Id, vm.Integrity, CategoryType.Entry.IntegrityLevel);

                if (dbCatA == null)
                {
                    modelState.AddModelError<RecordEvaluationEditViewModel>(x => x.Integrity, "Dopad nenalezen.");
                }
                dbEntity.IntegrityLevelCategoryId = dbCatA.Id;
            }
            else
            {
                dbEntity.IntegrityLevelCategoryId = null;
            }
        }

        private static List<Tuple<string, List<string>>> CreateTablesRequired(List<GdprGAPCell> measuresForRisk, List<Category> impactCategories)
        {
            var vm = new List<Tuple<string, List<string>>>();

            string emptyMsg = "- bez požadavku -";

            var recordGAP = measuresForRisk.Where(x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.RecordMeasures);

            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.RecordMeasures.ToString(),
                    new List<string>()
                );
                foreach (var iC in impactCategories)
                {
                    var r = recordGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (recordGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(string.Join(
                                ", ",
                                recordGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.Name)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }

            var systemGAP = measuresForRisk.Where(x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.SystemMeasures);

            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.SystemMeasures.ToString(),
                    new List<string>()
                );
                foreach (var iC in impactCategories)
                {
                    var r = systemGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (systemGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(string.Join(
                                ", ",
                                systemGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.Name)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }

            var storageTechGAP = measuresForRisk.Where(x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures);

            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.StorageTechnicalMeasures.ToString(),
                    new List<string>()
                );
                foreach (var iC in impactCategories)
                {
                    var r = storageTechGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (storageTechGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(string.Join(
                                ", ",
                                storageTechGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.Name)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }

            var storageGAP = measuresForRisk.Where(x => x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures);

            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.StoragePhysicalMeasures.ToString(),
                    new List<string>()
                );
                foreach (var iC in impactCategories)
                {
                    var r = storageGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (storageGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(storageGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Sum(x => x.Category.Value) + " - " + string.Join(
                                ", ",
                                storageGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.NameWithValue)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }

            var nodeGAP = measuresForRisk.Where(x =>
                x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.NodeMeasures);
            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.NodeMeasures.ToString(),
                    new List<string>()
                );
                foreach (var iC in impactCategories)
                {
                    var r = nodeGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (nodeGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(nodeGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Sum(x => x.Category.Value) + " - " + string.Join(
                                ", ",
                                nodeGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.NameWithValue)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }

            var targetGAP = measuresForRisk.Where(x =>
                x.GdprSecurityMeasuresCategoryId == GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures);

            {
                var itemToAdd = new Tuple<string, List<string>>(
                    GdprSecurityMeasuresCategory.Entry.TargetNodeMeasures.ToString(),
                    new List<string>()
                );

                foreach (var iC in impactCategories)
                {
                    var r = targetGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id);
                    if (r != null)
                    {
                        if (targetGAP.SingleOrDefault(x => x.RiskCategoryId == iC.Id).SelectedCategory.Any())
                        {
                            itemToAdd.Item2.Add(targetGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Sum(x => x.Category.Value) + " - " + string.Join(
                                ", ",
                                targetGAP.SingleOrDefault(x => x.RiskCategory.Id == iC.Id)
                                    .SelectedCategory.Select(x => x.Category.NameWithValue)));
                        }
                        else
                        {
                            itemToAdd.Item2.Add(emptyMsg);
                        }
                    }
                    else
                    {
                        itemToAdd.Item2.Add(emptyMsg);
                    }
                }

                vm.Add(itemToAdd);
            }
            return vm;
        }

        public async Task<GdprGAPCell> CreateGdprGapCell(ModelStateDictionary modelState, GapSelectBoxColumn selectBoxVm)
        {
            var cellEntity = new GdprGAPCell
            {
                Id = Guid.NewGuid(),
                RiskAnalysisId = selectBoxVm.Id,
                UserCompanyId = await userService.GetCurrentCompanyIdAsync(),
                RiskCategoryId = new Guid(selectBoxVm.riskCategoryId),
                GdprSecurityMeasuresCategoryId = selectBoxVm.measureType,
            }.CreatedByUser(await userService.GetCurrentUserAsync());

            await UpdateGdprGapCell(modelState, cellEntity, selectBoxVm);
            unitOfWork.AddForInsert(cellEntity);

            return cellEntity;
        }

        private bool IsRelevant(RiskAnalysisEvaluatedEntity eval)
        {
            if (eval is null)
            {
                return false;
            }

            var list = new List<Category> { eval.Availability, eval.Integrity, eval.Confidentiality };
            if (list.Any(x => !(x is null)))
            {
                return true;
            }
            return false;
        }

        private static Category ReturnImpact(List<Category> evals, List<Category> impacts)
        {
            var max = evals.Where(x => x != null).Max(x => x.Value);
            return impacts.OrderBy(x => x.Value).FirstOrDefault(x => max != null && x.Value >= max);
        }

        #endregion
    }

    public static class GapServiceHelpers
    {
        public enum Action
        {
            Add = 0,
            Update,
            Remove,
        }
    }
}
