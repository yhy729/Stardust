﻿using System;
using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using NewLife;
using NewLife.Cube;
using Stardust.Data;
using XCode;
using XCode.Membership;

namespace Stardust.Web.Areas.Registries.Controllers
{
    [RegistryArea]
    public class AppController : EntityController<App>
    {
        static AppController()
        {
            MenuOrder = 99;

            ListFields.RemoveField("Secret");
        }

        protected override Boolean Valid(App entity, DataObjectMethodType type, Boolean post)
        {
            if (!post) return base.Valid(entity, type, post);

            var act = type switch
            {
                DataObjectMethodType.Update => "修改",
                DataObjectMethodType.Insert => "添加",
                DataObjectMethodType.Delete => "删除",
                _ => type + "",
            };

            // 必须提前写修改日志，否则修改后脏数据失效，保存的日志为空
            if (type == DataObjectMethodType.Update && (entity as IEntity).HasDirty)
                LogProvider.Provider.WriteLog(act, entity);

            var err = "";
            try
            {
                return base.Valid(entity, type, post);
            }
            catch (Exception ex)
            {
                err = ex.Message;
                throw;
            }
            finally
            {
                LogProvider.Provider.WriteLog(act, entity, err);
            }
        }

        /// <summary>启用禁用下线告警</summary>
        /// <param name="enable"></param>
        /// <returns></returns>
        [EntityAuthorize(PermissionFlags.Update)]
        public ActionResult SetAlarm(Boolean enable = true)
        {
            foreach (var item in SelectKeys)
            {
                var dt = App.FindById(item.ToInt());
                if (dt != null)
                {
                    dt.AlarmOnOffline = enable;
                    dt.Save();
                }
            }

            return JsonRefresh("操作成功！");
        }
    }
}