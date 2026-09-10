using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Сервіс перерахунку агрегованих показників планів і навантажень.
public sealed class AggregatesService
{
    private readonly AppDbContext _db;

    public AggregatesService(AppDbContext db)
    {
        _db = db;
    }

    private static Dictionary<TKey, int> BuildCountLookup<TItem, TKey>(
        IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector,
        Func<TItem, int> hoursSelector)
        where TKey : notnull
        => items
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Sum(hoursSelector));

    // Перераховує години для модульних планів і навантаження викладачів.
    public async Task RecalcAsync(
        IEnumerable<(int CourseId, int ModuleId)>? plans = null,
        IEnumerable<(int TeacherId, int CourseId)>? loads = null,
        CancellationToken cancellationToken = default)
    {
        var lessonTypes = await _db.LessonTypes
            .Select(lt => new { lt.Id, lt.Code, lt.CountInPlan, lt.CountInLoad })
            .ToListAsync(cancellationToken);
        var excludePlanIds = lessonTypes
            .Where(lt =>
                !lt.CountInPlan
                || string.Equals(lt.Code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();
        var excludeLoadIds = lessonTypes
            .Where(lt =>
                !lt.CountInLoad
                || string.Equals(lt.Code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        if (plans is null)
        {
            var allPlans = await _db.ModulePlans.ToListAsync(cancellationToken);
            var courseIds = allPlans.Select(p => p.CourseId).Distinct().ToList();
            var moduleIds = allPlans.Select(p => p.ModuleId).Distinct().ToList();
            var items = await _db.ScheduleItems
                .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                             && courseIds.Contains(si.Group.CourseId)
                             && moduleIds.Contains(si.ModuleId))
                .Select(si => new CurriculumScheduleRow(
                    si.Id,
                    si.Group.CourseId,
                    si.BatchKey,
                    si.Date,
                    si.StartTime,
                    si.EndTime,
                    si.GroupId,
                    si.ModuleId,
                    si.LessonTypeId,
                    si.ModuleTopicId,
                    si.TeacherId,
                    si.RoomId,
                    si.IsSelfStudy))
                .ToListAsync(cancellationToken);
            var counts = BuildCountLookup(
                CurriculumScheduleAggregation.CollapseForPlan(items),
                item => (item.CourseId, item.ModuleId),
                item => CurriculumScheduleAggregation.ScheduledHours(item.StartTime, item.EndTime));

            foreach (var plan in allPlans)
            {
                plan.ScheduledHours = counts.GetValueOrDefault((plan.CourseId, plan.ModuleId));
            }
        }
        else
        {
            var keys = plans.Distinct().ToList();
            if (keys.Count > 0)
            {
                var items = await ApplyExactPlanScope(
                        _db.ScheduleItems.Where(si => !excludePlanIds.Contains(si.LessonTypeId)),
                        keys)
                    .Select(si => new CurriculumScheduleRow(
                        si.Id,
                        si.Group.CourseId,
                        si.BatchKey,
                        si.Date,
                        si.StartTime,
                        si.EndTime,
                        si.GroupId,
                        si.ModuleId,
                        si.LessonTypeId,
                        si.ModuleTopicId,
                        si.TeacherId,
                        si.RoomId,
                        si.IsSelfStudy))
                    .ToListAsync(cancellationToken);
                var counts = BuildCountLookup(
                    CurriculumScheduleAggregation.CollapseForPlan(items),
                    item => (item.CourseId, item.ModuleId),
                    item => CurriculumScheduleAggregation.ScheduledHours(item.StartTime, item.EndTime));
                var plansToUpdate = await ApplyExactPlanScope(_db.ModulePlans, keys)
                    .ToListAsync(cancellationToken);

                foreach (var plan in plansToUpdate)
                {
                    plan.ScheduledHours = counts.GetValueOrDefault((plan.CourseId, plan.ModuleId));
                }
            }
        }

        if (loads is null)
        {
            var activeLoads = await _db.TeacherCourseLoads
                .Where(l => l.IsActive)
                .ToListAsync(cancellationToken);
            var teacherIds = activeLoads.Select(l => l.TeacherId).Distinct().ToList();
            var courseIds = activeLoads.Select(l => l.CourseId).Distinct().ToList();
            var items = await _db.ScheduleItems
                .Where(si => si.TeacherId != null
                             && !excludeLoadIds.Contains(si.LessonTypeId)
                             && teacherIds.Contains(si.TeacherId!.Value)
                             && courseIds.Contains(si.Group.CourseId))
                .Select(si => new CurriculumScheduleRow(
                    si.Id,
                    si.Group.CourseId,
                    si.BatchKey,
                    si.Date,
                    si.StartTime,
                    si.EndTime,
                    si.GroupId,
                    si.ModuleId,
                    si.LessonTypeId,
                    si.ModuleTopicId,
                    si.TeacherId,
                    si.RoomId,
                    si.IsSelfStudy))
                .ToListAsync(cancellationToken);
            var counts = BuildCountLookup(
                CurriculumScheduleAggregation.CollapseForTeacherLoad(items),
                item => (TeacherId: item.TeacherId!.Value, item.CourseId),
                item => CurriculumScheduleAggregation.ScheduledHours(item.StartTime, item.EndTime));

            foreach (var load in activeLoads)
            {
                load.ScheduledHours = counts.GetValueOrDefault((load.TeacherId, load.CourseId));
            }

            await _db.TeacherCourseLoads
                .Where(l => !l.IsActive && l.ScheduledHours != 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(l => l.ScheduledHours, 0),
                    cancellationToken);
        }
        else
        {
            var keys = loads.Distinct().ToList();
            if (keys.Count > 0)
            {
                var items = await ApplyExactTeacherLoadScope(
                        _db.ScheduleItems.Where(si => si.TeacherId != null
                                                      && !excludeLoadIds.Contains(si.LessonTypeId)),
                        keys)
                    .Select(si => new CurriculumScheduleRow(
                        si.Id,
                        si.Group.CourseId,
                        si.BatchKey,
                        si.Date,
                        si.StartTime,
                        si.EndTime,
                        si.GroupId,
                        si.ModuleId,
                        si.LessonTypeId,
                        si.ModuleTopicId,
                        si.TeacherId,
                        si.RoomId,
                        si.IsSelfStudy))
                    .ToListAsync(cancellationToken);
                var counts = BuildCountLookup(
                    CurriculumScheduleAggregation.CollapseForTeacherLoad(items),
                    item => (TeacherId: item.TeacherId!.Value, item.CourseId),
                    item => CurriculumScheduleAggregation.ScheduledHours(item.StartTime, item.EndTime));
                var loadsToUpdate = await ApplyExactTeacherLoadScope(
                        _db.TeacherCourseLoads.Where(load => load.IsActive),
                        keys)
                    .ToListAsync(cancellationToken);

                foreach (var load in loadsToUpdate)
                {
                    load.ScheduledHours = counts.GetValueOrDefault((load.TeacherId, load.CourseId));
                }

                await ApplyExactTeacherLoadScope(
                        _db.TeacherCourseLoads.Where(load => !load.IsActive && load.ScheduledHours != 0),
                        keys)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(load => load.ScheduledHours, 0),
                        cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    // Будує один SQL-предикат із точних областей курсу, не розширюючи пари до декартового добутку.
    private static IQueryable<ScheduleItem> ApplyExactPlanScope(
        IQueryable<ScheduleItem> source,
        IReadOnlyCollection<(int CourseId, int ModuleId)> keys)
        => source.Where(BuildExactPairPredicate<ScheduleItem>(
            keys.Select(key => (ScopeId: key.CourseId, ValueId: key.ModuleId)),
            item => item.Group.CourseId,
            item => item.ModuleId));

    private static IQueryable<ModulePlan> ApplyExactPlanScope(
        IQueryable<ModulePlan> source,
        IReadOnlyCollection<(int CourseId, int ModuleId)> keys)
        => source.Where(BuildExactPairPredicate<ModulePlan>(
            keys.Select(key => (ScopeId: key.CourseId, ValueId: key.ModuleId)),
            plan => plan.CourseId,
            plan => plan.ModuleId));

    private static IQueryable<ScheduleItem> ApplyExactTeacherLoadScope(
        IQueryable<ScheduleItem> source,
        IReadOnlyCollection<(int TeacherId, int CourseId)> keys)
        => source.Where(BuildExactPairPredicate<ScheduleItem>(
            keys.Select(key => (ScopeId: key.CourseId, ValueId: key.TeacherId)),
            item => item.Group.CourseId,
            item => item.TeacherId!.Value));

    private static IQueryable<TeacherCourseLoad> ApplyExactTeacherLoadScope(
        IQueryable<TeacherCourseLoad> source,
        IReadOnlyCollection<(int TeacherId, int CourseId)> keys)
        => source.Where(BuildExactPairPredicate<TeacherCourseLoad>(
            keys.Select(key => (ScopeId: key.CourseId, ValueId: key.TeacherId)),
            load => load.CourseId,
            load => load.TeacherId));

    private static Expression<Func<T, bool>> BuildExactPairPredicate<T>(
        IEnumerable<(int ScopeId, int ValueId)> keys,
        Expression<Func<T, int>> scopeSelector,
        Expression<Func<T, int>> valueSelector)
    {
        var parameter = Expression.Parameter(typeof(T), "item");
        var scopeValue = ReplaceParameter(scopeSelector, parameter);
        var itemValue = ReplaceParameter(valueSelector, parameter);
        Expression body = Expression.Constant(false);
        foreach (var scope in keys.GroupBy(key => key.ScopeId))
        {
            var valueIds = scope.Select(key => key.ValueId).Distinct().ToArray();
            var scopeMatches = Expression.Equal(scopeValue, Expression.Constant(scope.Key));
            var valueMatches = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Contains),
                new[] { typeof(int) },
                Expression.Constant(valueIds),
                itemValue);
            body = Expression.OrElse(body, Expression.AndAlso(scopeMatches, valueMatches));
        }

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression ReplaceParameter<T>(
        Expression<Func<T, int>> selector,
        ParameterExpression replacement)
        => new ParameterReplacementVisitor(selector.Parameters[0], replacement)
            .Visit(selector.Body)!;

    private sealed class ParameterReplacementVisitor(
        ParameterExpression source,
        ParameterExpression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? replacement : base.VisitParameter(node);
    }
}
