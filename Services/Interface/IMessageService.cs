namespace AcrfAnnotationTool.Services.Interface;

public interface IMessageService
{
    void Info(string message);

    void Success(string message);

    void Warning(string message);

    void Error(string message);
}
