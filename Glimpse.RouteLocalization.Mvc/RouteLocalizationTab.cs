namespace Glimpse.RouteLocalization.Mvc
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;
	using System.Web.Routing;
	using Glimpse.AspNet.AlternateType;
	using Glimpse.AspNet.Extensibility;
	using Glimpse.AspNet.Message;
	using Glimpse.AspNet.Model;
	using Glimpse.Core.Extensibility;
	using Glimpse.Core.Extensions;
	using Glimpse.Core.Tab.Assist;
	using global::RouteLocalization.Mvc.Extensions;
	using global::RouteLocalization.Mvc.Routing;
	using MvcRoute = System.Web.Routing.Route;
	using MvcRouteBase = System.Web.Routing.RouteBase;
	using MvcRouteValueDictionary = System.Web.Routing.RouteValueDictionary;

	// Copied and modified from https://github.com/Glimpse/Glimpse/blob/master/source/Glimpse.AspNet/Tab/Routes.cs
	public class RouteLocalizationTab : AspNetTab, ITabSetup, ITabLayout, IKey
	{
		private static readonly object layout = TabLayout.Create().Row(r =>
		{
			r.Cell(0).WidthInPixels(100);
			r.Cell(1).AsKey();
			r.Cell(2);

			r.Cell(3).WidthInPercent(20).SetLayout(TabLayout.Create().Row(x =>
			{
				x.Cell("{{0}} ({{1}})").WidthInPercent(45);
				x.Cell(2);
			}));

			r.Cell(4).WidthInPercent(35).SetLayout(TabLayout.Create().Row(x =>
			{
				x.Cell(0).WidthInPercent(30);
				x.Cell(1);
				x.Cell(2).WidthInPercent(30);
			}));

			r.Cell(5).WidthInPercent(15).SetLayout(TabLayout.Create().Row(x =>
			{
				x.Cell(0).WidthInPercent(45);
				x.Cell(1).WidthInPercent(55);
			}));

			r.Cell(6).WidthInPixels(100).Suffix(" ms").Class("mono").AlignRight();
		}).Build();

		public string Key
		{
			get
			{
				return "glimpse_localization_routes";
			}
		}

		public override string Name
		{
			get
			{
				return "LocalizationRoutes";
			}
		}

		public override object GetData(ITabContext context)
		{
			Dictionary<int, List<RouteDataMessage>> routeMessages = ProcessMessages(context.GetMessages<RouteDataMessage>());
			Dictionary<int, Dictionary<int, List<ProcessConstraintMessage>>> constraintMessages =
				ProcessMessages(context.GetMessages<ProcessConstraintMessage>());

			List<RouteModel> result = new List<RouteModel>();

			using (RouteTable.Routes.GetReadLock())
			{
				foreach (MvcRouteBase routeBase in RouteTable.Routes)
				{
					if (routeBase.GetType().ToString() == "System.Web.Mvc.Routing.LinkGenerationRoute")
					{
						continue;
					}

					if (routeBase.GetType().ToString() == "System.Web.Mvc.Routing.RouteCollectionRoute")
					{
						// This catches any routing that has been defined using Attribute Based Routing
						// System.Web.Http.Routing.RouteCollectionRoute is a collection of HttpRoutes
						object subRoutes =
							routeBase.GetType().GetField("_subRoutes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(routeBase);
						IList<MvcRoute> routes =
							(IList<MvcRoute>)
								subRoutes.GetType().GetField("_routes", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(subRoutes);

						for (int i = 0; i < routes.Count; i++)
						{
							RouteModel routeModel = GetRouteModelForRoute(context, routes[i], routeMessages, constraintMessages);
							result.Add(routeModel);
						}
					}
					else
					{
						RouteModel routeModel = GetRouteModelForRoute(context, routeBase, routeMessages, constraintMessages);

						result.Add(routeModel);
					}
				}
			}

			return result;
		}

		public object GetLayout()
		{
			return layout;
		}

		public void Setup(ITabSetupContext context)
		{
			context.PersistMessages<ProcessConstraintMessage>();
			context.PersistMessages<RouteDataMessage>();
		}

		private static TSource SafeFirstOrDefault<TSource>(IEnumerable<TSource> source)
		{
			if (source == null)
			{
				return default(TSource);
			}

			return source.FirstOrDefault();
		}

		private RouteModel GetRouteModelForRoute(ITabContext context, MvcRouteBase routeBase,
			Dictionary<int, List<RouteDataMessage>> routeMessages,
			Dictionary<int, Dictionary<int, List<ProcessConstraintMessage>>> constraintMessages)
		{
			RouteModel routeModel = new RouteModel();

			RouteDataMessage routeMessage = SafeFirstOrDefault(routeMessages.GetValueOrDefault(routeBase.GetHashCode()));
			if (routeMessage != null)
			{
				routeModel.Duration = routeMessage.Duration;
				routeModel.IsMatch = routeMessage.IsMatch;
			}

			MvcRoute route = routeBase as MvcRoute;
			if (route != null)
			{
				routeModel.Area = (route.DataTokens != null && route.DataTokens.ContainsKey("area"))
					? route.DataTokens["area"].ToString() : null;
				routeModel.Url = route.Url;

				// Append localizations if possible
				LocalizationCollectionRoute localizationCollectionRoute = null;
				FieldInfo fieldInfo = route.GetType().GetField("__target");

				if (fieldInfo != null)
				{
					localizationCollectionRoute = fieldInfo.GetValue(route) as LocalizationCollectionRoute;
				}

				if (localizationCollectionRoute != null)
				{
					routeModel.Url += " (LocalizationRoute)";
					routeModel.Url += Environment.NewLine;

					foreach (LocalizationRoute localizationRoute in
						localizationCollectionRoute.LocalizedRoutes.OrderBy(x => x.Culture))
					{
						routeModel.Url += string.Format(Environment.NewLine + "{0} ({1})", localizationRoute.Url(),
							!string.IsNullOrEmpty(localizationRoute.Culture) ? localizationRoute.Culture : "neutral");
					}

					if (!localizationCollectionRoute.LocalizedRoutes.Any())
					{
						routeModel.Url += Environment.NewLine + "! No translations exists - this route will not be accessible !";
					}
				}

				routeModel.RouteData = ProcessRouteData(route.Defaults, routeMessage);
				routeModel.Constraints = ProcessConstraints(context, route, constraintMessages);
				routeModel.DataTokens = ProcessDataTokens(route.DataTokens);
			}
			else
			{
				routeModel.Url = routeBase.ToString();
			}

			IRouteNameMixin routeName = routeBase as IRouteNameMixin;
			if (routeName != null)
			{
				routeModel.Name = routeName.Name;
			}

			return routeModel;
		}

		private IEnumerable<RouteConstraintModel> ProcessConstraints(ITabContext context, MvcRoute route,
			Dictionary<int, Dictionary<int, List<ProcessConstraintMessage>>> constraintMessages)
		{
			if (route.Constraints == null || route.Constraints.Count == 0)
			{
				return null;
			}

			Dictionary<int, List<ProcessConstraintMessage>> counstraintRouteMessages =
				constraintMessages.GetValueOrDefault(route.GetHashCode());

			List<RouteConstraintModel> result = new List<RouteConstraintModel>();
			foreach (KeyValuePair<string, object> constraint in route.Constraints)
			{
				RouteConstraintModel model = new RouteConstraintModel();
				model.ParameterName = constraint.Key;
				model.Constraint = constraint.Value.ToString();

				if (counstraintRouteMessages != null)
				{
					ProcessConstraintMessage counstraintMessage =
						SafeFirstOrDefault(counstraintRouteMessages.GetValueOrDefault(constraint.Value.GetHashCode()));
					model.IsMatch = false;

					if (counstraintMessage != null)
					{
						model.IsMatch = counstraintMessage.IsMatch;
					}
				}

				result.Add(model);
			}

			return result;
		}

		private IDictionary<string, object> ProcessDataTokens(IDictionary<string, object> dataTokens)
		{
			return dataTokens != null && dataTokens.Count > 0 ? dataTokens : null;
		}

		private Dictionary<int, List<RouteDataMessage>> ProcessMessages(IEnumerable<RouteDataMessage> messages)
		{
			if (messages == null)
			{
				return new Dictionary<int, List<RouteDataMessage>>();
			}

			return messages.GroupBy(x => x.RouteHashCode).ToDictionary(x => x.Key, x => x.ToList());
		}

		private Dictionary<int, Dictionary<int, List<ProcessConstraintMessage>>> ProcessMessages(
			IEnumerable<ProcessConstraintMessage> messages)
		{
			if (messages == null)
			{
				return new Dictionary<int, Dictionary<int, List<ProcessConstraintMessage>>>();
			}

			return messages.GroupBy(x => x.RouteHashCode)
				.ToDictionary(x => x.Key,
					x => x.ToList().GroupBy(y => y.ConstraintHashCode).ToDictionary(y => y.Key, y => y.ToList()));
		}

		private IEnumerable<RouteDataItemModel> ProcessRouteData(MvcRouteValueDictionary dataDefaults,
			RouteDataMessage routeMessage)
		{
			if (dataDefaults == null || dataDefaults.Count == 0)
			{
				return null;
			}

			List<RouteDataItemModel> routeData = new List<RouteDataItemModel>();
			foreach (KeyValuePair<string, object> dataDefault in dataDefaults)
			{
				RouteDataItemModel routeDataItemModel = new RouteDataItemModel();
				routeDataItemModel.PlaceHolder = dataDefault.Key;
				routeDataItemModel.DefaultValue = dataDefault.Value;

				if (routeMessage != null && routeMessage.Values != null)
				{
					routeDataItemModel.ActualValue = routeMessage.Values[dataDefault.Key];
				}

				routeData.Add(routeDataItemModel);
			}

			return routeData;
		}
	}
}