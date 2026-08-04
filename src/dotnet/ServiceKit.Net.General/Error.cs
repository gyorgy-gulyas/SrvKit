namespace ServiceKit.Net
{
    // One thing that went wrong. Deliberately NOT a status: a status describes the whole answer and
    // the transport can only carry one of them, so it lives on the Response. An answer may well
    // carry several errors - a form with three bad fields is the ordinary case, not the exception.
    public class Error
    {
        // Which field it is about, in the caller's own terms: "items[1].quantity",
        // "billingAddress.country". Empty when the error is not about a field, such as
        // "this order does not exist" - and that is what lets a UI mark the right control instead
        // of showing a sentence.
        public string Path { get; set; } = string.Empty;

        public string MessageText { get; set; }

        public string AdditionalInformation { get; set; }
    }
}
