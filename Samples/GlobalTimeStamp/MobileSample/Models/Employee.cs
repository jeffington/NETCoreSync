﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NETCoreSync;

namespace MobileSample.Models
{
    [SyncSchema(MapToClassName = "SyncEmployee")]
    public class Employee
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        [SyncProperty(PropertyIndicator = SyncPropertyAttribute.PropertyIndicatorEnum.Id)]
        public string Id { get; set; }

        [SyncFriendlyId]
        public string Name { get; set; }

        public DateTime Birthday { get; set; }

        public int NumberOfComputers { get; set; }

        public decimal SavingAmount { get; set; }

        public bool IsActive { get; set; }

        public string DepartmentId { get; set; }
        public Department Department { get; set; }

        [SyncProperty(PropertyIndicator = SyncPropertyAttribute.PropertyIndicatorEnum.LastUpdated)]
        public long LastUpdated { get; set; }

        [SyncProperty(PropertyIndicator = SyncPropertyAttribute.PropertyIndicatorEnum.Deleted)]
        public long? Deleted { get; set; }

        public override string ToString()
        {
            return $"{nameof(Id)}: {Id}, {nameof(Name)}: {Name}, {nameof(Birthday)}: {Birthday.ToString("dd-MMM-yyyy")}, {nameof(NumberOfComputers)}: {NumberOfComputers}, {nameof(SavingAmount)}: {SavingAmount.ToString("#,#0.00")}, {nameof(IsActive)}: {IsActive}, {nameof(Department)}: {Department?.Id}, {nameof(LastUpdated)}: {LastUpdated}, {nameof(Deleted)}: {(Deleted == null ? "null" : Convert.ToString(Deleted.Value))}";
        }
    }
}
