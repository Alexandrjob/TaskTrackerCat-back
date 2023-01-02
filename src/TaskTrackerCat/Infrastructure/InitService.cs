﻿using Dapper;
using Microsoft.Data.SqlClient;
using TaskTrackerCat.Infrastructure.Factories.Interfaces;
using TaskTrackerCat.Repositories.Models;

namespace TaskTrackerCat.Infrastructure;

public class InitService
{
    #region Fields

    private readonly ILogger<InitService> _logger;
    private readonly IServiceProvider _serviceProvider;

    private SqlConnection _connection;
    private readonly List<DietDto> _diets;

    private int NUMBER_MEALS_PER_DAY;
    private TimeSpan START_ESTIMATED_TIMESPAN_FEEDING;
    private TimeSpan END_ESTIMATED_TIMESPAN_FEEDING;

    private int countServingNumber = 1;
    private DateTime estimatedDateFeeding;
    private int daysInMonth;
    private TimeSpan INTERVAL;

    #endregion
    
    public InitService(ILogger<InitService> logger, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _diets = new List<DietDto>();
    }

    public async Task Init()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbConnectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory<SqlConnection>>();
        _connection = await dbConnectionFactory.CreateConnection();

        await InitConfig();
        await InitMonth();
        _diets.Clear();
        await InitNextMonth();
    }

    private async Task InitConfig()
    {
        var config = new ConfigDto()
        {
            NumberMealsPerDay = 7,
            StartFeeding = new TimeSpan(7, 30, 0),
            EndFeeding = new TimeSpan(23, 00, 0)
        };
        NUMBER_MEALS_PER_DAY = config.NumberMealsPerDay;
        START_ESTIMATED_TIMESPAN_FEEDING = config.StartFeeding;
        END_ESTIMATED_TIMESPAN_FEEDING = config.EndFeeding;
        SetInterval();

        //Проверка на существование данных в таблице.
        var sqlCheck = @"SELECT count(*) FROM config";
        var resultIsEmpty = await _connection.QueryAsync<bool>(sqlCheck);

        if (resultIsEmpty.FirstOrDefault())
        {
            _logger.LogInformation("Конфигурация найдена в базе данных.");
            return;
        }

        var sqlInsert =
            "INSERT INTO config " +
            "(number_meals_per_day, start_feeding, end_feeding) " +
            "VALUES(@NumberMealsPerDay, @StartFeeding, @EndFeeding)";
        try
        {
            await _connection.ExecuteAsync(sqlInsert, config);
            _logger.LogInformation("Конфигурация записана в базу данных.");
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "Ошибка отправки данных(config) в базу данных.");
            throw;
        }
    }

    private void SetInterval()
    {
        var timeFeeding = END_ESTIMATED_TIMESPAN_FEEDING - START_ESTIMATED_TIMESPAN_FEEDING;
        var notRoundedInterval = timeFeeding / (NUMBER_MEALS_PER_DAY - 1);

        //Код взят с сайта. https://kkblog.ru/rounding-datetime-datestamp/
        //Округляем в меньшую сторону.
        var sec = notRoundedInterval.TotalSeconds;
        var divider = 5 * 60; // Делитель.
        //выравниваем секунды по началу интервала.
        var newSec = Math.Floor(sec / divider) * divider;
        //переводим секунды в такты.
        var newTicks = (long) newSec * 10000000;
        INTERVAL = new TimeSpan(newTicks);
    }

    private async Task InitMonth()
    {
        countServingNumber = 1;

        //Проверка на существование данных в таблице.
        var sql = @"SELECT count(*) FROM diets";
        var resultIsEmpty = await _connection.QueryAsync<bool>(sql);
        if (resultIsEmpty.FirstOrDefault())
        {
            _logger.LogInformation("Приемы пищи текущего месяца найдены в базе данных.");
            return;
        }

        //Дата приема еды начинается с текущего месяца.
        estimatedDateFeeding = new DateTime(
            DateTime.UtcNow.Year,
            DateTime.UtcNow.Month,
            1,
            START_ESTIMATED_TIMESPAN_FEEDING.Hours,
            START_ESTIMATED_TIMESPAN_FEEDING.Minutes,
            START_ESTIMATED_TIMESPAN_FEEDING.Milliseconds);
        //Количество дней в текущем месяце.
        daysInMonth = DateTime.DaysInMonth(DateTime.UtcNow.Year, DateTime.UtcNow.Month);

        await AddDiets();
        _logger.LogInformation("Приемы пищи текущего месяца({Date}) записаны в базу данных.", DateTime.UtcNow);
    }

    private async Task InitNextMonth()
    {
        countServingNumber = 1;

        //Проверка на существование будущего месяца.
        var sql = @"SELECT MAX(estimated_date_feeding) FROM diets";
        var result = await _connection.QueryAsync<DateTime>(sql);

        var maxMonth = result.FirstOrDefault().Month;
        var nextMonth = DateTime.UtcNow.AddMonths(1).Month;

        //Если максимальныая дата масяца совпадает с будущим месяцем.
        if (maxMonth == nextMonth)
        {
            _logger.LogInformation("Приемы пищи следующего месяца найдены в базе данных.");
            return;
        }

        //Дата приема еды начинается со следующего месяца.
        estimatedDateFeeding = new DateTime(
                DateTime.UtcNow.Year,
                DateTime.UtcNow.Month,
                1,
                START_ESTIMATED_TIMESPAN_FEEDING.Hours,
                START_ESTIMATED_TIMESPAN_FEEDING.Minutes,
                START_ESTIMATED_TIMESPAN_FEEDING.Milliseconds)
            .AddMonths(1);
        //Дней в следующем месяце.
        daysInMonth = DateTime.DaysInMonth(DateTime.UtcNow.Year, DateTime.UtcNow.AddMonths(1).Month);

        await AddDiets();
        _logger.LogInformation("Приемы пищи следующего месяца({Date}) записаны в базу данных.",
            DateTime.UtcNow.AddMonths(1));
    }

    private async Task AddDiets()
    {
        for (var i = 0; i < daysInMonth * NUMBER_MEALS_PER_DAY; i++) AddDiet();

        var sql =
            "INSERT INTO diets " +
            "(serving_number, status, estimated_date_feeding) " +
            "VALUES(@ServingNumber, @Status, @EstimatedDateFeeding)";
        try
        {
            await _connection.ExecuteAsync(sql, _diets);
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "Ошибка отправки данных(diets) в базу данных.");
            throw;
        }
    }

    private void AddDiet()
    {
        var diet = new DietDto()
        {
            ServingNumber = countServingNumber,
            Status = false,
            EstimatedDateFeeding = estimatedDateFeeding
        };

        if (countServingNumber == NUMBER_MEALS_PER_DAY)
        {
            SetEndTimeFeeding();
            diet.EstimatedDateFeeding = estimatedDateFeeding;
            _diets.Add(diet);

            SetStartTimeFeeding();
            estimatedDateFeeding = estimatedDateFeeding.AddDays(1); //Кормить каждый день.
            countServingNumber = 1;
            return;
        }

        estimatedDateFeeding = estimatedDateFeeding.AddHours(INTERVAL.Hours).AddMinutes(INTERVAL.Minutes);

        _diets.Add(diet);
        countServingNumber++;
    }

    private void SetStartTimeFeeding()
    {
        //Устанавливаем значения часа и минут в начальные значения.
        estimatedDateFeeding = new DateTime(
            estimatedDateFeeding.Year,
            estimatedDateFeeding.Month,
            estimatedDateFeeding.Day,
            START_ESTIMATED_TIMESPAN_FEEDING.Hours,
            START_ESTIMATED_TIMESPAN_FEEDING.Minutes,
            estimatedDateFeeding.Millisecond);
    }

    private void SetEndTimeFeeding()
    {
        estimatedDateFeeding = new DateTime(
            estimatedDateFeeding.Year,
            estimatedDateFeeding.Month,
            estimatedDateFeeding.Day,
            END_ESTIMATED_TIMESPAN_FEEDING.Hours,
            END_ESTIMATED_TIMESPAN_FEEDING.Minutes,
            estimatedDateFeeding.Millisecond);
    }
}