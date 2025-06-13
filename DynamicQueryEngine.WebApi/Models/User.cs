namespace DynamicQueryEngine.WebApi.Models;

public class User
{
    public string NationalIdNumber { get; set; }
    public string LoginName { get; set; }
    public string RegNo { get; set; }
    public string Id { get; set; }
    public string Title { get; set; }
    public string CompanyCode { get; set; }
    public bool IsActive { get; set; }
}