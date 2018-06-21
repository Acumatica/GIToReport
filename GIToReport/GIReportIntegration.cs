using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PX.Api;
using PX.Common;
using PX.Data.Description.GI;
using PX.Data.Description;
using System.Web.Compilation;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using PX.Reports;
using PX.Reports.Controls;
using FileInfo = PX.SM.FileInfo;
using SortOrder = PX.Reports.SortOrder;
using Group = PX.Reports.Controls.Group;

namespace PX.Data.Maintenance.GI
{
	public class GIReportIntegration : PXGraphExtension<GenericInquiryDesigner>
	{
		private static readonly XmlSerializer _serializer = new XmlSerializer(typeof(Report));
		private static readonly Regex _nameRegex = new Regex("[^a-zA-Z0-9]",
			RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

		public PXAction<GIDesign> ExportAsReport;
		[PXButton]
		[PXUIField(DisplayName = "Export as Report")]
		public void exportAsReport()
		{
			var design = Base.Designs.Current;
			if (design == null) return;

			string reportName = _nameRegex.Replace(design.Name ?? "", "");
			if (String.IsNullOrEmpty(reportName))
				reportName = "Report1";

			var report = new Report
			{
				Name = reportName,
				SchemaUrl = PXUrl.SiteUrlWithPath(),
				Items =
				{
					new PageHeaderSection() { Name = "pageHeaderSection1" },
					new DetailSection() { Name = "detailSection1" },
					new PageFooterSection() { Name = "pageFooterSection1" }
				}
			};

			var tableByAlias = ConvertTables(report);
			ConvertRelations(report, tableByAlias);
			ConvertParameters(report);
			ConvertFilters(report);
			ConvertGroups(report);
			ConvertSorts(report);

			byte[] data = SerializeReport(report);
			var fileInfo = new FileInfo(reportName + ".rpx", null, data);
			throw new PXRedirectToFileException(fileInfo, true);
		}

		private IReadOnlyDictionary<string, Type> ConvertTables(Report report)
		{
			var graph = new PXGraph();
			var tableTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

			foreach (var table in Base.Tables.Select().RowCast<GITable>()
				.Where(t => !String.IsNullOrEmpty(t.Name) && !String.IsNullOrEmpty(t.Alias)))
			{
				Type type = PXBuildManager.GetType(table.Name, false);
				if (type == null) continue;

				tableTypes[table.Alias] = type;
			}

			foreach (var type in tableTypes.Values.Distinct())
			{
				var reportTable = new ReportTable(type.Name)
				{
					FullName = type.FullName,
				};

				PXCache cache = graph.Caches[type];
				foreach (string field in cache.Fields)
				{
					var fieldInfo = ApiFieldInfo.Create(cache, field);
					if (fieldInfo == null) continue;
					var reportField = new ReportField(field);
					TypeCode typeCode = Type.GetTypeCode(fieldInfo.DataType);
					if (typeCode != TypeCode.Empty)
						reportField.DataType = typeCode;
					reportTable.Fields.Add(reportField);
				}

				report.Tables.Add(reportTable);
			}

			return tableTypes;
		}

		private void ConvertRelations(Report report, IReadOnlyDictionary<string, Type> tableByAlias)
		{
			foreach (var relation in Base.Relations.Select().RowCast<GIRelation>()
				.Where(r => r.IsActive == true && !String.IsNullOrEmpty(r.ParentTable) && !String.IsNullOrEmpty(r.ChildTable)))
			{
				if (!tableByAlias.TryGetValue(relation.ParentTable, out Type parentType)
					|| !tableByAlias.TryGetValue(relation.ChildTable, out Type childType))
					continue;

				var reportRelation = new ReportRelation(parentType.Name, childType.Name)
				{
					ParentAlias = relation.ParentTable,
					ChildAlias = relation.ChildTable,
					JoinType = GetJoinType(relation.JoinType)
				};

				if (String.Equals(parentType.Name, relation.ParentTable, StringComparison.Ordinal))
					reportRelation.ParentAlias = null;

				if (String.Equals(childType.Name, relation.ChildTable, StringComparison.Ordinal))
					reportRelation.ChildAlias = null;

				foreach (var joinCondition in PXSelect<GIOn, Where<GIOn.designID, Equal<Current<GIDesign.designID>>,
						And<GIOn.relationNbr, Equal<Required<GIRelation.lineNbr>>>>>
					.Select(Base, relation.LineNbr)
					.RowCast<GIOn>()
					.Where(l => !String.IsNullOrEmpty(l.ParentField) && !String.IsNullOrEmpty(l.ChildField)
								&& !String.IsNullOrEmpty(l.Condition)))
				{
					var reportJoinCondition = new RelationRow(
						ReplaceParameters(NormalizeFieldName(joinCondition.ParentField)),
						ReplaceParameters(NormalizeFieldName(joinCondition.ChildField)))
					{
						OpenBraces = joinCondition.OpenBrackets?.Trim()?.Length ?? 0,
						CloseBraces = joinCondition.CloseBrackets?.Trim()?.Length ?? 0,
						Operator = String.Equals(joinCondition.Operation?.Trim(), "O", StringComparison.OrdinalIgnoreCase)
							? FilterOperator.Or
							: FilterOperator.And,
						Condition = GetLinkCondition(joinCondition.Condition),
					};

					reportRelation.Links.Add(reportJoinCondition);
				}

				report.Relations.Add(reportRelation);
			}
		}

		private void ConvertParameters(Report report)
		{
			foreach (var parameter in Base.Parameters.Select().RowCast<GIFilter>()
				.Where(p => p.IsActive == true && !String.IsNullOrEmpty(p.Name)))
			{
				var reportParameter = new ReportParameter(parameter.Name, ParameterType.String)
				{
					Required = parameter.Required == true,
					ColumnSpan = parameter.ColSpan ?? 1,
					DefaultValue = parameter.DefaultValue,
					Nullable = parameter.Required != true,
					Visible = parameter.Hidden != true,
					Prompt = parameter.DisplayName,
				};

				if (parameter.FieldName == typeof(CheckboxCombobox.checkbox).FullName)
				{
					reportParameter.Type = ParameterType.Boolean;
					reportParameter.Nullable = false;
				}
				else if (parameter.FieldName == typeof(CheckboxCombobox.combobox).FullName
						 && !String.IsNullOrEmpty(parameter.AvailableValues))
				{
					string[] splitted = parameter.AvailableValues.Split(',');
					foreach (string pairStr in splitted)
					{
						string[] pair = pairStr.Split(new[] { ';' }, 2);
						if (pair.Length == 2)
						{
							reportParameter.ValidValues.Add(new ParameterValue(pair[0], pair[1]));
						}
					}
				}
				else
				{
					reportParameter.ViewName = $"=Report.GetFieldSchema('{NormalizeFieldName(parameter.FieldName)}')";
				}

				report.Parameters.Add(reportParameter);
			}
		}

		private void ConvertFilters(Report report)
		{
			foreach (var condition in Base.Wheres.Select().RowCast<GIWhere>()
				.Where(c => c.IsActive == true && !String.IsNullOrEmpty(c.DataFieldName) && !String.IsNullOrEmpty(c.Condition)))
			{
				var reportCondition = new FilterExp(ReplaceParameters(NormalizeFieldName(condition.DataFieldName)), 
					GetFilterCondition(condition.Condition))
				{
					OpenBraces = condition.OpenBrackets?.Trim()?.Length ?? 0,
					CloseBraces = condition.CloseBrackets?.Trim()?.Length ?? 0,
					Operator = String.Equals(condition.Operation?.Trim(), "O", StringComparison.OrdinalIgnoreCase)
						? FilterOperator.Or
						: FilterOperator.And,
					Value = ReplaceParameters(condition.Value1),
					Value2 = ReplaceParameters(condition.Value2)
				};

				report.Filters.Add(reportCondition);
			}
		}

		private void ConvertSorts(Report report)
		{
			foreach (var sort in Base.Sortings.Select().RowCast<GISort>()
				.Where(s => s.IsActive == true && !String.IsNullOrEmpty(s.DataFieldName) && !String.IsNullOrEmpty(s.SortOrder)))
			{
				var reportSort = new SortExp(NormalizeFieldName(sort.DataFieldName),
					String.Equals(sort.SortOrder?.Trim(), "A", StringComparison.OrdinalIgnoreCase)
						? SortOrder.Ascending
						: SortOrder.Descending);
				report.Sorting.Add(reportSort);
			}
		}

		private void ConvertGroups(Report report)
		{
			var reportGroupings = new List<GroupExp>();

			foreach (var grouping in Base.GroupBy.Select().RowCast<GIGroupBy>()
				.Where(g => g.IsActive == true && !String.IsNullOrEmpty(g.DataFieldName)))
			{
				var reportGrouping = new GroupExp(NormalizeFieldName(grouping.DataFieldName));
				reportGroupings.Add(reportGrouping);
			}

			if (reportGroupings.Count > 0)
			{
				var reportGroup = new Group() { Name = "group1" };
				reportGroup.Headers.Add(new GroupHeaderSection() { Name = "groupHeaderSection1" });
				reportGroup.Footers.Add(new GroupFooterSection() { Name = "groupFooterSection1" });
				reportGroup.Grouping.AddRange(reportGroupings);
				report.Groups.Add(reportGroup);
			}
		}

		private byte[] SerializeReport(Report report)
		{
			using (StringWriter writer = new StringWriter())
			{
				_serializer.Serialize(writer, report);
				var doc = XDocument.Parse(writer.ToString());
				doc.Declaration.Encoding = "utf-8";

				doc.Descendants(nameof(Report.OriginalFilters)).Remove();

				var xmlSections = new XElement("Sections");
				doc.Root?.Add(xmlSections);
				foreach (var item in report.Items.Cast<ReportItem>().Where(i => !(i is GroupSection)))
				{
					var xmlItem = new XElement(item.GetType().Name.Replace("Section", ""), new XAttribute("Name", item.Name));
					xmlSections.Add(xmlItem);
				}

				if (report.Groups.Count > 0)
				{
					var xmlGroups = new XElement("Groups");
					doc.Root?.Add(xmlGroups);

					foreach (Group group in report.Groups)
					{
						var xmlGroup = new XElement("Group", new XAttribute("Name", group.Name));
						var xmlGrouping = new XElement("Grouping");
						var xmlHeaders = new XElement("Headers");
						var xmlFooters = new XElement("Footers");
						xmlGroup.Add(xmlGrouping, xmlHeaders, xmlFooters);
						xmlGroups.Add(xmlGroup);

						foreach (var groupExp in group.Grouping)
						{
							var xmlGroupExp = new XElement("GroupExp", new XElement("DataField", groupExp.DataField));
							xmlGrouping.Add(xmlGroupExp);
						}

						foreach (GroupSection header in group.Headers)
						{
							var xmlHeader = new XElement("Header", new XAttribute("Name", header.Name));
							xmlHeaders.Add(xmlHeader);
						}

						foreach (GroupSection header in group.Footers)
						{
							var xmlFooter = new XElement("Footer", new XAttribute("Name", header.Name));
							xmlFooters.Add(xmlFooter);
						}
					}
				}

				using (Stream stream = new MemoryStream())
				{
					doc.Save(stream);
					stream.Seek(0, SeekOrigin.Begin);
					byte[] result = new byte[stream.Length];
					stream.Read(result, 0, result.Length);
					return result;
				}
			}
		}

		private JoinType GetJoinType(string joinType)
		{
			switch (joinType?.ToUpperInvariant()?.Trim())
			{
				case "L": return JoinType.Left;
				case "R": return JoinType.Right;
				case "F": return JoinType.Full;
				case "C": return JoinType.Cross;
				default: return JoinType.Inner;
			}
		}

		private LinkCondition GetLinkCondition(string condition)
		{
			switch (condition?.ToUpperInvariant()?.Trim())
			{
				case "E": return LinkCondition.Equal;
				case "NE": return LinkCondition.NotEqual;
				case "G": return LinkCondition.Greater;
				case "GE": return LinkCondition.GreaterOrEqual;
				case "L": return LinkCondition.Less;
				case "LE": return LinkCondition.LessOrEqual;
				case "NU": return LinkCondition.IsNull;
				case "NN": return LinkCondition.IsNotNull;
				default: throw new PXException(ErrorMessages.InvalidCondition, condition);
			}
		}

		private FilterCondition GetFilterCondition(string condition)
		{
			switch (condition?.ToUpperInvariant()?.Trim())
			{
				case "E": return FilterCondition.Equal;
				case "NE": return FilterCondition.NotEqual;
				case "G": return FilterCondition.Greater;
				case "GE": return FilterCondition.GreaterOrEqual;
				case "L": return FilterCondition.Less;
				case "LE": return FilterCondition.LessOrEqual;
				case "NU": return FilterCondition.IsNull;
				case "NN": return FilterCondition.IsNotNull;
				case "B": return FilterCondition.Between;
				case "LI": return FilterCondition.Like;
				case "NL": return FilterCondition.NotLike;
				case "RL": return FilterCondition.RLike;
				case "LL": return FilterCondition.LLike;
				default: throw new PXException(ErrorMessages.InvalidCondition, condition);
			}
		}

		private string CapitalizeFirstLetter(string fieldName)
		{
			if (String.IsNullOrEmpty(fieldName)) return fieldName;
			return Char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1, fieldName.Length - 1);
		}

		private string NormalizeFieldName(string fieldName)
		{
			if (String.IsNullOrEmpty(fieldName)) return fieldName;
			string[] splitted = fieldName.Split(new[] { '.' }, 2);
			if (splitted.Length == 2)
			{
				return splitted[0] + "." + CapitalizeFirstLetter(splitted[1]);
			}

			return CapitalizeFirstLetter(fieldName);
		}

		private string ReplaceParameters(string value)
		{
			if (String.IsNullOrEmpty(value)) return value;

			foreach (var parameter in Base.Parameters.Select().RowCast<GIFilter>())
			{
				value = value.Replace("[" + parameter.Name + "]", "@" + parameter.Name, StringComparison.OrdinalIgnoreCase);
			}

			return value;
		}
	}
}