using Sitecore.Analytics;
using Sitecore.Collections;
using Sitecore.ContentTesting.Caching;
using Sitecore.ContentTesting.Configuration;
using Sitecore.ContentTesting.Extensions;
using Sitecore.ContentTesting.Inspectors;
using Sitecore.ContentTesting.Model;
using Sitecore.ContentTesting.Model.Data.Items;
using Sitecore.ContentTesting.Models;
using Sitecore.Data;
using Sitecore.Data.Items;
using Sitecore.Pipelines.ItemProvider.GetItem;
using Sitecore.SecurityModel;
using Sitecore.Sites;
using System;
using System.Linq;
using Sitecore.ContentTesting;

namespace Sitecore.Support.ContentTesting.Pipelines.ItemProvider.GetItem
{
    public class GetItemUnderTestProcessor : Sitecore.Pipelines.ItemProvider.GetItem.GetItemProcessor
    {
        protected bool allowCache = true;

        protected readonly IContentTestingFactory factory;

        protected bool isContentTestingEnabled = true;

        public GetItemUnderTestProcessor() : this(true, null)
        {
        }

        public GetItemUnderTestProcessor(bool allowCache, IContentTestingFactory factory = null)
        {
            this.allowCache = allowCache;
            this.factory = (factory ?? ContentTestingFactory.Instance);
            this.isContentTestingEnabled = Settings.IsAutomaticContentTestingEnabled;
        }

        public override void Process(Sitecore.Pipelines.ItemProvider.GetItem.GetItemArgs args)
        {
            if (!this.isContentTestingEnabled)
            {
                return;
            }
            if (args.Handled)
            {
                return;
            }
            Sitecore.Data.Items.Item item = args.Result;
            if (item == null)
            {
                item = (((object)args.ItemId != null) ? args.FallbackProvider.GetItem(args.ItemId, args.Language, args.Version, args.Database, args.SecurityCheck) : args.FallbackProvider.GetItem(args.ItemPath, args.Language, args.Version, args.Database, args.SecurityCheck));
            }
            if (item == null)
            {
                return;
            }
            if (this.ShouldRun(args, item))
            {
                VersionRedirect versionRedirect = null;
                string key = string.Empty;
                IRequestCache<Sitecore.Data.Items.Item, VersionRedirect> versionRedirectionRequestCache = this.factory.VersionRedirectionRequestCache;
                if (this.allowCache)
                {
                    key = versionRedirectionRequestCache.GenerateKey(item);
                    versionRedirect = versionRedirectionRequestCache.Get(key);
                }
                if (versionRedirect == null)
                {
                    TestDefinitionItem testForItem = this.GetTestForItem(args, item);
                    if (testForItem != null && testForItem.IsRunning)
                    {
                        int? versionToExpose = this.GetVersionToExpose(args, item, testForItem);
                        if (versionToExpose.HasValue)
                        {
                            versionRedirect = new VersionRedirect
                            {
                                Version = versionToExpose.Value,
                                Redirect = true
                            };
                            if (this.allowCache)
                            {
                                versionRedirectionRequestCache.Put(key, versionRedirect);
                            }
                        }
                    }
                    else if (this.allowCache)
                    {
                        versionRedirectionRequestCache.Put(key, new VersionRedirect
                        {
                            Redirect = false
                        });
                    }
                }
                if (versionRedirect != null && versionRedirect.Redirect)
                {
                    Sitecore.Data.Version version = Sitecore.Data.Version.Parse(versionRedirect.Version);
                    item = args.FallbackProvider.GetItem(item.ID, item.Language, version, item.Database, args.SecurityCheck);
                }
            }
            args.Result = item;
        }

        protected virtual bool ShouldRun(Sitecore.Pipelines.ItemProvider.GetItem.GetItemArgs args, Sitecore.Data.Items.Item targetItem)
        {
            if (targetItem == null)
            {
                return false;
            }
            Sitecore.Sites.SiteContext site = Context.Site;
            if (site != null && site.Name == "shell")
            {
                return false;
            }
            bool flag = site == null || Context.PageMode.IsNormal;
            return args.Version == Sitecore.Data.Version.Latest && flag;
        }

        protected virtual TestDefinitionItem GetTestForItem(Sitecore.Pipelines.ItemProvider.GetItem.GetItemArgs args, Sitecore.Data.Items.Item item)
        {
            if (item.Language == null || string.IsNullOrEmpty(item.Language.Name))
            {
                return null;
            }
            string value = item.Fields[AnalyticsIds.PageLevelTestDefinitionField].GetValue(false, false);
            TestDefinitionItem testDefinitionItem = null;
            if (!string.IsNullOrEmpty(value))
            {
                Sitecore.Data.Items.Item item2 = args.FallbackProvider.GetItem(value, args.Language, Sitecore.Data.Version.Latest, args.Database, Sitecore.SecurityModel.SecurityCheck.Disable);
                if (item2 != null)
                {
                    testDefinitionItem = TestDefinitionItem.Create(item2);
                }
            }
            else
            {
                value = item.Fields[AnalyticsIds.ContentTestField].GetValue(false, false);
                if (!string.IsNullOrEmpty(value))
                {
                    Sitecore.Data.Items.Item item3 = args.FallbackProvider.GetItem(value, args.Language, Sitecore.Data.Version.Latest, args.Database, Sitecore.SecurityModel.SecurityCheck.Disable);
                    if (item3 != null && item3.Parent != null)
                    {
                        testDefinitionItem = TestDefinitionItem.Create(item3.Parent);
                    }
                }
            }
            if (testDefinitionItem != null)
            {
                Sitecore.Data.DataUri dataUri = testDefinitionItem.ParseContentItem();
                if (dataUri != null && !dataUri.IsSameItemAndLanguage(item))
                {
                    testDefinitionItem = null;
                }
            }

            // fix for bug #47136
            if (testDefinitionItem != null && testDefinitionItem.GetTestType() == TestType.Page)
            {
                testDefinitionItem = null;
            }
            // fix for bug #47136

            return testDefinitionItem;
        }

        protected virtual int? GetVersionToExpose(Sitecore.Pipelines.ItemProvider.GetItem.GetItemArgs args, Sitecore.Data.Items.Item hostItem, TestDefinitionItem testItem)
        {
            if (testItem.PageLevelTestVariables.Count > 0)
            {
                TestVariablesInspector testVariablesInspector = new TestVariablesInspector();
                Sitecore.Data.DataUri[] source = testVariablesInspector.GetContentTestDataSources(testItem.PageLevelTestVariables.First<ITestVariableItem>()).ToArray<Sitecore.Data.DataUri>();
                Sitecore.Collections.VersionCollection existedVersions = args.FallbackProvider.GetVersions(hostItem, args.Language);
                int[] source2 = (from x in source
                                 where existedVersions.Any((Sitecore.Data.Version y) => y.Number == x.Version.Number)
                                 select x.Version.Number).ToArray<int>();
                if (source2.Any<int>())
                {
                    return new int?(source2.Min());
                }
            }
            return null;
        }
    }
}
