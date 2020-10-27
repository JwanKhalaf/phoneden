namespace Phoneden.Services
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DataAccess.Context;
  using Entities;
  using Entities.Shared;
  using Interfaces;
  using Microsoft.EntityFrameworkCore;
  using ViewModels;
  using ViewModels.SaleOrders;

  public class ReportService : IReportService
  {
    private readonly PdContext _context;
    private readonly int _recordsPerPage;

    public ReportService(IPaginationConfiguration paginationSettings, PdContext context)
    {
      _context = context ?? throw new ArgumentNullException(nameof(context));
      _recordsPerPage = paginationSettings.RecordsPerPage;
    }

    public InventoryReportViewModel GetProducts(int page, InventoryReportSearchViewModel search)
    {
      int totalNumberOfProducts = 0;

      if (HaveSearchTermsChanged(search))
      {
        page = 1;
      }

      IQueryable<Product> products = _context.Products
        .Include(p => p.Category)
        .Include(p => p.Brand)
        .Include(p => p.Quality)
        .AsNoTracking()
        .Where(p => !p.IsDeleted);

      if (!string.IsNullOrEmpty(search.SearchTerm))
      {
        string searchTerm = search
          .SearchTerm
          .Trim()
          .ToLowerInvariant();

        switch (search.Category)
        {
          case SearchCategory.Category:
            products = products
              .Where(p => EF.Functions.Like(p.Category.Name.ToLowerInvariant(), $"%{searchTerm}%"))
              .OrderBy(p => p.Category.Name);

            totalNumberOfProducts = products.Count();

            products = products
              .Skip(_recordsPerPage * (page - 1))
              .Take(_recordsPerPage);
            break;
          case SearchCategory.Brand:
            products = products
              .Where(p => EF.Functions.Like(p.Brand.Name.ToLowerInvariant(), $"%{searchTerm}%"))
              .OrderBy(p => p.Brand.Name);

            totalNumberOfProducts = products.Count();

            products = products
              .Skip(_recordsPerPage * (page - 1))
              .Take(_recordsPerPage);
            break;
        }
      }
      else
      {
        products = products.OrderBy(p => p.Quantity);
        totalNumberOfProducts = products.Count();

        products = products
          .Skip(_recordsPerPage * (page - 1))
          .Take(_recordsPerPage);
      }

      PaginationViewModel paginationVm = new PaginationViewModel();
      paginationVm.CurrentPage = page;
      paginationVm.RecordsPerPage = _recordsPerPage;
      paginationVm.TotalRecords = totalNumberOfProducts;

      InventoryReportViewModel inventoryReportVm = new InventoryReportViewModel();
      inventoryReportVm.Products = ProductViewModelFactory.BuildList(products.ToList());
      inventoryReportVm.Pagination = paginationVm;
      inventoryReportVm.Search = search;

      TrackCurrentSearchTerm(inventoryReportVm);
      return inventoryReportVm;
    }

    public IEnumerable<CustomerViewModel> GetTopTenCustomers()
    {
      IQueryable<Customer> customers = (from c in _context.Customers
                                        where c.SaleOrders.Count > 0
                                        let orderSum = c.SaleOrders.Where(so => so.Status != SaleOrderStatus.Cancelled).Sum(so => so.LineItems.Sum(li => li.Quantity * li.Price))
                                        orderby orderSum descending
                                        select c).Take(10);
      IEnumerable<CustomerViewModel> customerVms = CustomerViewModelFactory.BuildList(customers.ToList());
      return customerVms;
    }

    public IEnumerable<SupplierViewModel> GetTopTenSuppliers()
    {
      IQueryable<Supplier> suppliers = (from s in _context.Suppliers
                                        where s.PurchaseOrders.Count > 0
                                        let orderSum = s.PurchaseOrders.Where(po => po.Status != PurchaseOrderStatus.Cancelled).Sum(po => po.LineItems.Sum(li => li.Quantity * li.Price))
                                        orderby orderSum descending
                                        select s).AsNoTracking().Take(10);
      List<SupplierViewModel> supplierVms = SupplierViewModelFactory.CreateList(suppliers.ToList());
      return supplierVms;
    }

    public SalesReportViewModel GetSaleOrders(int page, DateTime startDate, DateTime endDate)
    {
      if (startDate == null)
      {
        throw new ArgumentNullException(nameof(startDate), @"The start date cannot be null!");
      }

      if (startDate > DateTime.UtcNow)
      {
        throw new ArgumentException(@"The start date cannot be set in the future!", nameof(startDate));
      }

      IQueryable<SaleOrder> saleOrders = _context
        .SaleOrders
        .Include(so => so.Customer)
        .Include(so => so.LineItems)
        .Include(so => so.Invoice)
        .ThenInclude(i => i.Payments)
        .Include(so => so.Invoice)
        .ThenInclude(i => i.Returns)
        .ThenInclude(r => r.Product)
        .AsNoTracking()
        .Where(so => so.Date >= startDate && so.Date <= endDate && !so.IsDeleted)
        .OrderByDescending(so => so.Date)
        .Skip(_recordsPerPage * (page - 1))
        .Take(_recordsPerPage);

      List<SaleOrderViewModel> saleOrderVms = SaleOrderViewModelFactory.BuildList(saleOrders.ToList());

      foreach (SaleOrderViewModel saleOrder in saleOrderVms)
      {
        CalculateSaleOrderProfit(saleOrder);
      }

      PaginationViewModel pagination = new PaginationViewModel();
      pagination.CurrentPage = 1;
      pagination.RecordsPerPage = _recordsPerPage;
      pagination.TotalRecords = _context
        .SaleOrders
        .Count(so => so.Date >= startDate && so.Date <= endDate && !so.IsDeleted);

      SalesReportViewModel saleReport = new SalesReportViewModel();
      saleReport.StartDate = startDate;
      saleReport.EndDate = endDate;
      saleReport.SaleOrders = saleOrderVms;
      saleReport.Pagination = pagination;

      if (!saleReport.SaleOrders.Any())
      {
        saleReport.Total = 0;
      }
      else
      {
        saleReport.Total = saleOrderVms
          .Sum(so => so.Invoice.Amount - so.Invoice.Returns.Sum(r => r.Value));
      }

      saleReport.Profit = saleOrderVms.Sum(so => so.Profit);

      return saleReport;
    }

    public OutstandingInvoicesReportViewModel GetOutstandingInvoices(int page, DateTime startDate, DateTime endDate)
    {
      if (startDate == null)
      {
        throw new ArgumentNullException(nameof(startDate), @"The start date cannot be null!");
      }

      if (startDate > DateTime.UtcNow)
      {
        throw new ArgumentException(@"The start date cannot be set in the future!", nameof(startDate));
      }

      List<PurchaseOrderInvoice> outstandingInvoices = (from invoice in _context.PurchaseOrderInvoices.Include(i => i.Payments)
                                                        let totalPaidSoFar = invoice.Payments.Any() ? invoice.Payments.Sum(p => p.Currency == Currency.Gbp ? p.Amount : p.Amount / p.ConversionRate) : 0
                                                        where !invoice.IsDeleted && invoice.DueDate >= startDate && invoice.DueDate <= endDate && totalPaidSoFar < invoice.Amount
                                                        orderby invoice.DueDate descending
                                                        select invoice)
        .AsNoTracking()
        .Skip(_recordsPerPage * (page - 1))
        .Take(_recordsPerPage).ToList();

      List<PurchaseOrderInvoiceViewModel> outstandingInvoiceVms = PurchaseOrderInvoiceViewModelFactory.BuildList(outstandingInvoices);

      foreach (PurchaseOrderInvoiceViewModel invoice in outstandingInvoiceVms)
      {
        string businessName = _context
          .PurchaseOrders
          .Where(so => so.Id == invoice.PurchaseOrderId)
          .Select(so => so.Supplier.Name)
          .First();

        invoice.Business.Name = businessName;
      }

      decimal moneyUnpaid = outstandingInvoiceVms.Sum(invoice => invoice.RemainingAmount);

      OutstandingInvoicesReportViewModel reportVm = new OutstandingInvoicesReportViewModel
      {
        StartDate = startDate,
        EndDate = endDate,
        Invoices = outstandingInvoiceVms,
        Pagination = new PaginationViewModel
        {
          CurrentPage = page,
          RecordsPerPage = _recordsPerPage,
          TotalRecords = _context
          .PurchaseOrderInvoices
          .Count(i => i.DueDate >= startDate && i.DueDate <= endDate && !i.IsDeleted)
        },
        Total = moneyUnpaid
      };

      return reportVm;
    }

    private static void TrackCurrentSearchTerm(InventoryReportViewModel viewModel)
    {
      viewModel.Search.PreviousSearchTerm = viewModel.Search.SearchTerm;
    }

    private static bool HaveSearchTermsChanged(InventoryReportSearchViewModel searchVm)
    {
      return !string.Equals(searchVm.SearchTerm, searchVm.PreviousSearchTerm);
    }

    private void CalculateSaleOrderProfit(SaleOrderViewModel saleOrder)
    {
      decimal totalProfitAcrossAllLineItems = 0;

      foreach (SaleOrderLineItemViewModel lineItem in saleOrder.LineItems)
      {
        Product product = _context
          .Products
          .FirstOrDefault(p => p.Id == lineItem.ProductId);

        if (product != null)
        {
          totalProfitAcrossAllLineItems += (lineItem.Price - product.UnitCostPrice) * lineItem.Quantity;
        }
      }

      saleOrder.Profit = totalProfitAcrossAllLineItems;
    }
  }
}