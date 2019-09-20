﻿using System.Collections.Generic;

namespace SenseNet.OData.Responses
{
    public class ODataChildrenCollectionResponse : ODataResponse
    {
        public override ODataResponseType Type => ODataResponseType.ChildrenCollection;

        public IEnumerable<ODataContent> Value { get; set; }
        public override object GetValue() => Value;

        public ODataChildrenCollectionResponse(IEnumerable<ODataContent> value)
        {
            Value = value;
        }
    }
}